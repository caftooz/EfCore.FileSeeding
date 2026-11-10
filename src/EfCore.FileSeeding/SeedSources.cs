using System.Reflection;

namespace EfCore.JsonSeeding;

internal enum SeedFormat
{
    Json,
    Csv
}

/// <summary>
/// Один файл с данными: <see cref="Name"/> — имя таблицы или класса сущности (имя файла без расширения).
/// </summary>
internal sealed record SeedFile(string Name, string Origin, SeedFormat Format, Func<Stream> Open)
{
    /// <summary>
    /// Формат по расширению: <c>.json</c> или <c>.csv</c>; для остальных файлов — <see langword="null"/>.
    /// </summary>
    public static SeedFormat? DetectFormat(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".json" => SeedFormat.Json,
            ".csv" => SeedFormat.Csv,
            _ => null
        };
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
            yield return new SeedFile(
                name,
                $"{assembly.GetName().Name}: {resourceName}",
                format,
                () => assembly.GetManifestResourceStream(resourceName)
                      ?? throw new JsonSeedingException($"Не удалось открыть ресурс {resourceName}."));
        }
    }

    private static string Normalize(string value) => value.Replace('/', '.').Replace('\\', '.');
}

/// <summary>
/// Файлы *.json и *.csv из папки на диске.
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
            .Select(file => new SeedFile(
                Path.GetFileNameWithoutExtension(file), file, SeedFile.DetectFormat(file)!.Value, () => File.OpenRead(file)));
    }
}
