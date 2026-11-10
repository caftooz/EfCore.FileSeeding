using System.Reflection;

namespace EfCore.JsonSeeding;

internal enum SeedFormat
{
    Json,
    Csv,
    Xlsx
}

/// <summary>
/// Одна таблица с данными: <see cref="Name"/> — имя таблицы или класса сущности. Для JSON и CSV это
/// имя файла без расширения, для Excel — имя листа (<see cref="Sheet"/>).
/// </summary>
internal sealed record SeedFile(string Name, string Origin, SeedFormat Format, Func<Stream> Open, string? Sheet = null)
{
    /// <summary>
    /// Формат по расширению; для остальных файлов — <see langword="null"/>.
    /// Временные файлы Excel (<c>~$Книга.xlsx</c>) пропускаются.
    /// </summary>
    public static SeedFormat? DetectFormat(string fileName)
    {
        if (fileName.Replace('\\', '/').Split('/', '.').Any(part => part.StartsWith("~$", StringComparison.Ordinal)))
            return null;

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".json" => SeedFormat.Json,
            ".csv" => SeedFormat.Csv,
            ".xlsx" => SeedFormat.Xlsx,
            _ => null
        };
    }

    /// <summary>
    /// Файл → таблицы: книга Excel даёт по таблице на каждый лист с данными,
    /// кроме служебных листов, имя которых начинается с «_».
    /// </summary>
    public static IEnumerable<SeedFile> Expand(string name, string origin, SeedFormat format, Func<Stream> open)
    {
        if (format != SeedFormat.Xlsx)
            return [new SeedFile(name, origin, format, open)];

        byte[] bytes;
        using (var stream = open())
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }

        return XlsxReader.GetSheetNames(bytes, origin)
            .Where(sheet => !sheet.StartsWith('_'))
            .Select(sheet => new SeedFile(sheet.Trim(), $"{origin}, лист «{sheet}»", format, open, sheet))
            .ToList();
    }
}

internal interface ISeedSource
{
    IEnumerable<SeedFile> GetFiles();
}

/// <summary>
/// Файлы, встроенные в сборку. Поддерживаются оба способа именования ресурсов:
/// по умолчанию (<c>MyApp.Data.Seed.Demo.Products.json</c>) и через LogicalName (<c>Data/Seed/Demo/Products.json</c>).
/// </summary>
internal sealed class EmbeddedResourceSource(Assembly assembly, string folder) : ISeedSource
{
    public IEnumerable<SeedFile> GetFiles()
    {
        var prefix = Normalize(folder).Trim('.') + ".";

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (SeedFile.DetectFormat(resource) is not { } format)
                continue;

            var normalized = Normalize(resource);
            int start;
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                start = prefix.Length;
            }
            else
            {
                var index = normalized.IndexOf("." + prefix, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                    continue;
                start = index + 1 + prefix.Length;
            }

            var name = Path.GetFileNameWithoutExtension(normalized[start..]);
            if (name.Length == 0 || name.Contains('.'))
                continue;   // файлы во вложенных папках не берём

            var resourceName = resource;
            var files = SeedFile.Expand(
                name,
                $"{assembly.GetName().Name}: {resourceName}",
                format,
                () => assembly.GetManifestResourceStream(resourceName)
                      ?? throw new JsonSeedingException($"Не удалось открыть ресурс {resourceName}."));
            foreach (var file in files)
                yield return file;
        }
    }

    private static string Normalize(string value) => value.Replace('/', '.').Replace('\\', '.');
}

/// <summary>
/// Файлы *.json, *.csv и *.xlsx из папки на диске.
/// </summary>
internal sealed class DirectorySource(string path) : ISeedSource
{
    public IEnumerable<SeedFile> GetFiles()
    {
        var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
        if (!Directory.Exists(fullPath))
            throw new JsonSeedingException($"Папка с данными не найдена: {fullPath}");

        return Directory
            .EnumerateFiles(fullPath)
            .Where(file => SeedFile.DetectFormat(file) is not null)
            .Order(StringComparer.OrdinalIgnoreCase)
            .SelectMany(file => SeedFile.Expand(
                Path.GetFileNameWithoutExtension(file), file, SeedFile.DetectFormat(file)!.Value, () => File.OpenRead(file)));
    }
}
