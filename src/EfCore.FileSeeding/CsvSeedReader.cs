using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfCore.JsonSeeding;

/// <summary>
/// Читает CSV-таблицу (первая строка — названия столбцов) и превращает каждую строку в такую же запись,
/// как в JSON. Дальше записи проходят те же проверки, что и JSON.
/// </summary>
/// <remarks>
/// Рассчитано на файлы из Excel: разделитель <c>;</c>, <c>,</c> или табуляция определяется по заголовку,
/// кодировка — UTF-8 (с BOM или без) или Windows-1251, десятичная запятая допускается.
/// </remarks>
internal static class CsvSeedReader
{
    private static readonly char[] Delimiters = [';', '\t', ','];

    public static List<(JsonElement Element, string Where)> Read(byte[] bytes, string origin, IEntityType type)
    {
        var text = Decode(bytes);
        var rows = Parse(text, DetectDelimiter(text));
        var records = new List<(JsonElement, string)>();
        if (rows.Count == 0)
            return records;

        var headers = ReadHeader(rows[0], origin);
        var properties = headers.Select(h => h.Length == 0 ? null : JsonSeeder.FindProperty(type, h)).ToArray();

        foreach (var (cells, line) in rows.Skip(1))
        {
            if (cells.All(string.IsNullOrWhiteSpace))
                continue;   // пустые строки, в том числе «;;;» из Excel

            var where = $"{origin}, строка {line}";
            var record = new JsonObject();

            for (var i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                if (i >= headers.Length || headers[i].Length == 0)
                {
                    if (cell.Trim().Length == 0)
                        continue;
                    throw new JsonSeedingException(
                        $"{where}: значение «{cell}» в столбце №{i + 1}, у которого нет названия в первой строке.");
                }

                if (cell.Length == 0)
                    continue;   // пустая ячейка = значение по умолчанию

                var value = ConvertCell(cell, properties[i], headers[i], where);
                if (value is not null)
                    record[headers[i]] = value;
            }

            records.Add((JsonSerializer.SerializeToElement(record), where));
        }

        return records;
    }

    private static string Decode(byte[] bytes)
    {
        var start = bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException)
        {
            // Не UTF-8 — значит, «CSV (разделители — точка с запятой)» из русского Excel
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    private static char DetectDelimiter(string text)
    {
        var end = text.IndexOf('\n');
        var header = end < 0 ? text : text[..end];
        var best = ',';
        var bestCount = 0;
        foreach (var delimiter in Delimiters)
        {
            var count = header.Count(c => c == delimiter);
            if (count > bestCount)
            {
                best = delimiter;
                bestCount = count;
            }
        }

        return best;
    }

    /// <summary>
    /// Разбор по RFC 4180: значения в кавычках могут содержать разделитель и переносы строк,
    /// кавычка внутри кавычек записывается двумя: <c>"Сок ""Яблочный"""</c>.
    /// </summary>
    private static List<(List<string> Cells, int Line)> Parse(string text, char delimiter)
    {
        var rows = new List<(List<string>, int)>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var rowLine = 1;

        void EndCell()
        {
            cells.Add(cell.ToString());
            cell.Clear();
        }

        void EndRow()
        {
            EndCell();
            rows.Add((cells, rowLine));
            cells = [];
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (c == '\n')
                        line++;
                    if (c != '\r')
                        cell.Append(c);
                }
            }
            else if (c == '"' && cell.Length == 0)
            {
                inQuotes = true;
            }
            else if (c == delimiter)
            {
                EndCell();
            }
            else if (c == '\n')
            {
                EndRow();
                line++;
                rowLine = line;
            }
            else if (c != '\r')
            {
                cell.Append(c);
            }
        }

        if (cell.Length > 0 || cells.Count > 0)
            EndRow();

        return rows;
    }

    private static string[] ReadHeader((List<string> Cells, int Line) row, string origin)
    {
        var headers = row.Cells.Select(h => h.Trim()).ToArray();
        var duplicate = headers
            .Where(h => h.Length > 0)
            .GroupBy(h => h, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new JsonSeedingException($"{origin}, строка 1: столбец «{duplicate.Key}» указан дважды.");
        return headers;
    }

    /// <summary>
    /// Значение ячейки по типу свойства. Столбцы, которых нет в сущности (например, <c>password</c>
    /// для Transform), остаются строками.
    /// </summary>
    private static JsonNode? ConvertCell(string cell, IProperty? property, string column, string where)
    {
        var type = property is null ? typeof(string) : Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        if (type == typeof(string))
            return JsonValue.Create(cell);

        var value = cell.Trim();
        if (value.Length == 0)
            return null;

        if (IsInteger(type))
        {
            if (long.TryParse(RemoveSpaces(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                return JsonValue.Create(number);
            throw Invalid(where, column, cell, "целое число");
        }

        if (type == typeof(decimal) || type == typeof(double) || type == typeof(float))
        {
            if (decimal.TryParse(NormalizeDecimal(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return JsonValue.Create(number);
            throw Invalid(where, column, cell, "число");
        }

        if (type == typeof(bool))
        {
            return value.ToLowerInvariant() switch
            {
                "true" or "1" or "да" or "yes" => JsonValue.Create(true),
                "false" or "0" or "нет" or "no" => JsonValue.Create(false),
                _ => throw Invalid(where, column, cell, "true/false, 1/0 или да/нет")
            };
        }

        if (type.IsEnum && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var enumNumber))
            return JsonValue.Create(enumNumber);

        // enum по имени, даты (2026-11-10), Guid и прочее — строкой, как в JSON
        return JsonValue.Create(value);
    }

    private static bool IsInteger(Type type) =>
        type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ||
        type == typeof(sbyte) || type == typeof(uint) || type == typeof(ushort);

    private static string RemoveSpaces(string value) =>
        value.Replace(" ", "").Replace(" ", "").Replace(" ", "");

    /// <summary>
    /// <c>25,50</c>, <c>1 234,5</c>, <c>1.234,5</c> и <c>1,234.5</c> → формат с точкой.
    /// Если есть и точка, и запятая, десятичным считается последний знак.
    /// </summary>
    private static string NormalizeDecimal(string value)
    {
        value = RemoveSpaces(value);
        var comma = value.LastIndexOf(',');
        var dot = value.LastIndexOf('.');
        if (comma >= 0 && dot >= 0)
            return comma > dot ? value.Replace(".", "").Replace(',', '.') : value.Replace(",", "");
        return value.Replace(',', '.');
    }

    private static JsonSeedingException Invalid(string where, string column, string cell, string expected) =>
        new($"{where}: в столбце «{column}» значение «{cell}», ожидается {expected}.");
}
