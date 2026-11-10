using System.Reflection;

namespace EfCore.JsonSeeding;

/// <summary>
/// Один JSON-файл: <see cref="Name"/> — имя таблицы или класса сущности (имя файла без .json).
/// </summary>
internal sealed record SeedFile(string Name, string Origin, Func<Stream> Open);

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
    private const string Extension = ".json";

    public IEnumerable<SeedFile> GetFiles()
    {
        var prefix = Normalize(folder).Trim('.') + ".";

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
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

            var name = normalized[start..^Extension.Length];
            if (name.Length == 0 || name.Contains('.'))
                continue;   // файлы во вложенных папках не берём

            var resourceName = resource;
            yield return new SeedFile(
                name,
                $"{assembly.GetName().Name}: {resourceName}",
                () => assembly.GetManifestResourceStream(resourceName)
                      ?? throw new JsonSeedingException($"Не удалось открыть ресурс {resourceName}."));
        }
    }

    private static string Normalize(string value) => value.Replace('/', '.').Replace('\\', '.');
}

/// <summary>
/// Файлы *.json из папки на диске.
/// </summary>
internal sealed class DirectorySource(string path) : ISeedSource
{
    public IEnumerable<SeedFile> GetFiles()
    {
        var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
        if (!Directory.Exists(fullPath))
            throw new JsonSeedingException($"Папка с данными не найдена: {fullPath}");

        return Directory
            .EnumerateFiles(fullPath, "*.json")
            .Select(file => new SeedFile(Path.GetFileNameWithoutExtension(file), file, () => File.OpenRead(file)));
    }
}
