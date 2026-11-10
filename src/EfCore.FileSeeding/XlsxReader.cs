using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace EfCore.FileSeeding;

/// <summary>
/// Минимальное чтение .xlsx без сторонних библиотек: файл — это zip с XML внутри.
/// Читаются только значения ячеек; формулы — по последнему посчитанному Excel значению.
/// </summary>
internal static class XlsxReader
{
    private const string RelationshipsNamespaceSuffix = "/relationships";

    /// <summary>
    /// Листы с данными по порядку. Пустые листы (например, «Лист1» по умолчанию) не возвращаются.
    /// </summary>
    public static List<string> GetSheetNames(byte[] bytes, string origin)
    {
        using var workbook = Open(bytes, origin);
        return GetSheets(workbook, origin)
            .Where(sheet => HasData(workbook, sheet.Path, origin))
            .Select(sheet => sheet.Name)
            .ToList();
    }

    public static List<TableRow> ReadSheet(byte[] bytes, string sheetName, string origin)
    {
        using var workbook = Open(bytes, origin);
        var sheetPath = GetSheets(workbook, origin)
                            .Where(s => s.Name == sheetName)
                            .Select(s => s.Path)
                            .FirstOrDefault()
                        ?? throw new FileSeedingException($"{origin}: лист «{sheetName}» не найден.");
        var sharedStrings = ReadSharedStrings(workbook, origin);
        var document = Load(workbook, sheetPath, origin)
                       ?? throw new FileSeedingException($"{origin}: не найден {sheetPath}.");

        var rows = new List<TableRow>();
        var nextRow = 1;
        foreach (var row in document.Descendants().Where(e => e.Name.LocalName == "row"))
        {
            var line = int.TryParse(row.Attribute("r")?.Value, out var r) ? r : nextRow;
            nextRow = line + 1;

            var cells = new List<TableCell>();
            foreach (var cell in row.Elements().Where(e => e.Name.LocalName == "c"))
            {
                var column = ColumnIndex(cell.Attribute("r")?.Value) ?? cells.Count;
                while (cells.Count < column)
                    cells.Add(new TableCell(""));   // пропущенные пустые ячейки
                cells.Add(ReadCell(cell, sharedStrings, $"{origin}, ячейка {cell.Attribute("r")?.Value}"));
            }

            rows.Add(new TableRow(cells, line));
        }

        return rows;
    }

    private static ZipArchive Open(byte[] bytes, string origin)
    {
        try
        {
            return new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new FileSeedingException(
                $"{origin}: это не файл .xlsx. Старый формат .xls не поддерживается — сохраните как «Книга Excel (.xlsx)».", ex);
        }
    }

    private static XDocument? Load(ZipArchive workbook, string path, string origin)
    {
        var entry = workbook.GetEntry(path);
        if (entry is null)
            return null;
        try
        {
            using var stream = entry.Open();
            return XDocument.Load(stream);
        }
        catch (XmlException ex)
        {
            throw new FileSeedingException($"{origin}: повреждён {path}. {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Имена листов из xl/workbook.xml и пути к их XML из xl/_rels/workbook.xml.rels.
    /// </summary>
    private static List<(string Name, string Path)> GetSheets(ZipArchive workbook, string origin)
    {
        var book = Load(workbook, "xl/workbook.xml", origin)
                   ?? throw new FileSeedingException($"{origin}: в файле нет xl/workbook.xml — это не книга Excel.");
        var relations = Load(workbook, "xl/_rels/workbook.xml.rels", origin)?
            .Descendants()
            .Where(e => e.Name.LocalName == "Relationship")
            .ToDictionary(e => (string?)e.Attribute("Id") ?? "", e => (string?)e.Attribute("Target") ?? "")
            ?? new Dictionary<string, string>();

        var sheets = new List<(string, string)>();
        foreach (var sheet in book.Descendants().Where(e => e.Name.LocalName == "sheet"))
        {
            var name = (string?)sheet.Attribute("name") ?? "";
            var id = sheet.Attributes()
                .FirstOrDefault(a => a.Name.LocalName == "id" && a.Name.NamespaceName.EndsWith(RelationshipsNamespaceSuffix))
                ?.Value;
            if (id is null || !relations.TryGetValue(id, out var target))
                continue;

            var path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            sheets.Add((name, path));
        }

        return sheets;
    }

    private static bool HasData(ZipArchive workbook, string path, string origin) =>
        Load(workbook, path, origin)?
            .Descendants()
            .Any(e => e.Name.LocalName is "v" or "t" && e.Value.Trim().Length > 0) == true;

    private static List<string> ReadSharedStrings(ZipArchive workbook, string origin)
    {
        var document = Load(workbook, "xl/sharedStrings.xml", origin);
        if (document is null)
            return [];

        return document.Root!
            .Elements()
            .Where(e => e.Name.LocalName == "si")
            .Select(TextOf)
            .ToList();
    }

    /// <summary>
    /// Текст строки, в том числе с форматированием по частям (&lt;r&gt;&lt;t&gt;); подсказки произношения (rPh) пропускаются.
    /// </summary>
    private static string TextOf(XElement element) =>
        string.Concat(element
            .Descendants()
            .Where(e => e.Name.LocalName == "t" && e.Ancestors().All(a => a.Name.LocalName != "rPh"))
            .Select(e => e.Value));

    private static TableCell ReadCell(XElement cell, List<string> sharedStrings, string where)
    {
        var type = (string?)cell.Attribute("t");
        var value = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value;

        switch (type)
        {
            case "s":
                if (int.TryParse(value, out var index) && index >= 0 && index < sharedStrings.Count)
                    return new TableCell(sharedStrings[index]);
                throw new FileSeedingException($"{where}: повреждена ссылка на текст.");
            case "inlineStr":
                var inline = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "is");
                return new TableCell(inline is null ? "" : TextOf(inline));
            case "str" or "d":
                return new TableCell(value ?? "");
            case "b":
                return new TableCell(value == "1" ? "true" : "false");
            case "e":
                throw new FileSeedingException($"{where}: ошибка в формуле ({value}).");
            default:
                return value is null ? new TableCell("") : new TableCell(value, IsNumber: true);
        }
    }

    /// <summary>
    /// «C5» → 2 (номер столбца с нуля).
    /// </summary>
    private static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
            return null;

        var index = 0;
        var letters = 0;
        foreach (var c in reference.ToUpper(CultureInfo.InvariantCulture))
        {
            if (c is < 'A' or > 'Z')
                break;
            index = index * 26 + (c - 'A' + 1);
            letters++;
        }

        return letters == 0 ? null : index - 1;
    }
}
