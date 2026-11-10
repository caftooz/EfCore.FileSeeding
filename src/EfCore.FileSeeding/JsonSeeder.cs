using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfCore.JsonSeeding;

/// <summary>
/// Загружает JSON-файлы в базу. Таблицы, ключи и порядок загрузки берутся из модели EF,
/// поэтому для новой таблицы достаточно добавить файл.
/// </summary>
internal sealed class JsonSeeder(JsonSeedingOptions options)
{
    private const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public async Task SeedAsync(DbContext db, CancellationToken ct)
    {
        var lookup = BuildNameLookup(db.Model);
        var order = BuildDependencyOrder(db.Model);
        var seededTypes = new HashSet<IEntityType>();

        foreach (var source in options.Sources)
        {
            var files = source.GetFiles()
                .Select(file => (File: file, Type: Resolve(lookup, file, db)))
                .OrderBy(x => order[x.Type])
                .ToList();

            foreach (var (file, type) in files)
            {
                var (added, updated) = await SeedFileAsync(db, file, type, ct);
                seededTypes.Add(type);
                options.Log?.Invoke($"{file.Name}: добавлено {added}, обновлено {updated}");
            }
        }

        if (options.ResetSequences && db.Database.ProviderName == NpgsqlProvider)
            await ResetPostgresSequencesAsync(db, seededTypes, ct);
    }

    private async Task<(int Added, int Updated)> SeedFileAsync(
        DbContext db, SeedFile file, IEntityType type, CancellationToken ct)
    {
        JsonDocument document;
        await using (var stream = file.Open())
        {
            try
            {
                document = await JsonDocument.ParseAsync(stream, DocumentOptions, ct);
            }
            catch (JsonException ex)
            {
                throw new JsonSeedingException($"{file.Origin}: некорректный JSON. {ex.Message}", ex);
            }
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonSeedingException($"{file.Origin}: ожидается массив записей [ {{ ... }}, {{ ... }} ].");

            var key = type.FindPrimaryKey()!;
            var transforms = options.Transforms.Where(t => t.EntityType.IsAssignableFrom(type.ClrType)).ToList();
            var added = 0;
            var updated = 0;
            var number = 0;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                number++;
                var where = $"{file.Origin}, запись №{number}";

                if (element.ValueKind != JsonValueKind.Object)
                    throw new JsonSeedingException($"{where}: ожидается объект {{ ... }}.");
                if (options.Strict)
                    CheckUnknownProperties(element, type, transforms, where);

                var entity = Deserialize(element, type, where);

                var keyValues = key.Properties.Select(p => GetValue(p, entity)).ToArray();
                if (keyValues.Any(IsDefault))
                {
                    throw new JsonSeedingException(
                        $"{where}: не указан первичный ключ ({string.Join(", ", key.Properties.Select(p => p.Name))}). " +
                        "Без ключа при каждом запуске добавлялся бы дубликат.");
                }

                foreach (var transform in transforms)
                {
                    if (TryGetProperty(element, transform.JsonProperty, out var value))
                        transform.Apply(entity, value);
                }

                var existing = await db.FindAsync(type.ClrType, keyValues, ct);
                if (existing is null)
                {
                    db.Add(entity);
                    ApplyShadowProperties(db, entity, element, type, where);
                    added++;
                }
                else if (options.UpdateExisting && ApplyChanges(db, existing, entity, element, type, where))
                {
                    updated++;
                }
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                throw new JsonSeedingException(
                    $"{file.Origin}: база отклонила данные. {ex.InnerException?.Message ?? ex.Message}", ex);
            }

            db.ChangeTracker.Clear();
            return (added, updated);
        }
    }

    private static object Deserialize(JsonElement element, IEntityType type, string where)
    {
        try
        {
            return element.Deserialize(type.ClrType, SerializerOptions)
                   ?? throw new JsonSeedingException($"{where}: пустая запись.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new JsonSeedingException($"{where}: не удалось прочитать запись как {type.ClrType.Name}. {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Обновляет у существующей записи только те поля, которые явно указаны в JSON.
    /// </summary>
    private static bool ApplyChanges(
        DbContext db, object existing, object incoming, JsonElement element, IEntityType type, string where)
    {
        var entry = db.Entry(existing);
        var changed = false;

        foreach (var jsonProperty in element.EnumerateObject())
        {
            var property = FindProperty(type, jsonProperty.Name);
            if (property is null || property.IsPrimaryKey())
                continue;

            var newValue = property.IsShadowProperty()
                ? ReadValue(jsonProperty.Value, property, where)
                : GetValue(property, incoming);

            var current = entry.Property(property.Name);
            if (!Equals(current.CurrentValue, newValue))
            {
                current.CurrentValue = newValue;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Теневые свойства (есть в модели EF, но нет в классе) нельзя заполнить десериализацией —
    /// задаём их через EF после добавления записи.
    /// </summary>
    private static void ApplyShadowProperties(DbContext db, object entity, JsonElement element, IEntityType type, string where)
    {
        foreach (var property in type.GetProperties().Where(p => p.IsShadowProperty()))
        {
            if (TryGetProperty(element, property.Name, out var value))
                db.Entry(entity).Property(property.Name).CurrentValue = ReadValue(value, property, where);
        }
    }

    private static object? ReadValue(JsonElement value, IProperty property, string where)
    {
        try
        {
            return value.Deserialize(property.ClrType, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new JsonSeedingException($"{where}: поле «{property.Name}» не удалось прочитать как {property.ClrType.Name}.", ex);
        }
    }

    private static void CheckUnknownProperties(
        JsonElement element, IEntityType type, IReadOnlyList<TransformRule> transforms, string where)
    {
        foreach (var jsonProperty in element.EnumerateObject())
        {
            if (FindProperty(type, jsonProperty.Name) is not null)
                continue;
            if (transforms.Any(t => string.Equals(t.JsonProperty, jsonProperty.Name, StringComparison.OrdinalIgnoreCase)))
                continue;

            var known = string.Join(", ", type.GetProperties().Select(p => p.Name));
            throw new JsonSeedingException(
                $"{where}: неизвестное поле «{jsonProperty.Name}» (возможно, опечатка). " +
                $"Поля {type.ClrType.Name}: {known}. " +
                "Если поле нужно намеренно, обработайте его через Transform или включите AllowUnknownProperties().");
        }
    }

    private static IProperty? FindProperty(IEntityType type, string name) =>
        type.GetProperties().FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.GetColumnName(), name, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static object? GetValue(IProperty property, object entity) =>
        property.PropertyInfo is { } propertyInfo
            ? propertyInfo.GetValue(entity)
            : property.FieldInfo?.GetValue(entity);

    private static bool IsDefault(object? value) =>
        value is null || (value.GetType().IsValueType && value.Equals(Activator.CreateInstance(value.GetType())));

    /// <summary>
    /// Имя файла → сущность. Подходит и имя таблицы (<c>Products</c>), и имя класса (<c>Product</c>).
    /// </summary>
    private static Dictionary<string, IEntityType> BuildNameLookup(IModel model)
    {
        var lookup = new Dictionary<string, IEntityType>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in model.GetEntityTypes())
        {
            if (type.IsOwned() || type.FindPrimaryKey() is null)
                continue;

            lookup.TryAdd(type.ClrType.Name, type);
            if (type.BaseType is null && type.GetTableName() is { } table)
                lookup.TryAdd(table, type);
        }

        return lookup;
    }

    private static IEntityType Resolve(Dictionary<string, IEntityType> lookup, SeedFile file, DbContext db)
    {
        if (lookup.TryGetValue(file.Name, out var type))
            return type;

        var known = string.Join(", ", lookup.Keys.Order());
        throw new JsonSeedingException(
            $"{file.Origin}: в {db.GetType().Name} нет таблицы или сущности «{file.Name}». " +
            $"Имя файла должно совпадать с одним из: {known}.");
    }

    /// <summary>
    /// Сначала таблицы, на которые ссылаются, потом те, что ссылаются: Categories → Products.
    /// </summary>
    private static Dictionary<IEntityType, int> BuildDependencyOrder(IModel model)
    {
        var order = new Dictionary<IEntityType, int>();
        var visiting = new HashSet<IEntityType>();

        void Visit(IEntityType type)
        {
            if (order.ContainsKey(type) || !visiting.Add(type))
                return;   // уже обработан или циклическая ссылка

            if (type.BaseType is { } baseType)
                Visit(baseType);
            foreach (var foreignKey in type.GetForeignKeys())
            {
                if (foreignKey.PrincipalEntityType != type)
                    Visit(foreignKey.PrincipalEntityType);
            }

            order[type] = order.Count;
        }

        foreach (var type in model.GetEntityTypes())
            Visit(type);

        return order;
    }

    /// <summary>
    /// Записи вставлены с явными id, а счётчик PostgreSQL об этом не знает. Сдвигаем его за максимум,
    /// иначе следующая обычная вставка получит уже занятый id.
    /// </summary>
    private static async Task ResetPostgresSequencesAsync(DbContext db, IEnumerable<IEntityType> types, CancellationToken ct)
    {
        foreach (var type in types)
        {
            var key = type.FindPrimaryKey();
            if (key is not { Properties.Count: 1 })
                continue;

            var property = key.Properties[0];
            if (property.ValueGenerated != ValueGenerated.OnAdd || !IsInteger(property.ClrType))
                continue;

            var tableName = type.GetTableName();
            if (tableName is null)
                continue;

            var table = QuoteTable(type.GetSchema(), tableName);
            var column = property.GetColumnName();

            // Имена берутся из модели EF, а не из пользовательского ввода, и дополнительно экранируются
            var sql =
                $"SELECT setval(pg_get_serial_sequence('{EscapeLiteral(table)}', '{EscapeLiteral(column)}'), " +
                $"COALESCE((SELECT MAX({QuoteIdentifier(column)}) FROM {table}), 0) + 1, false)";
            await db.Database.ExecuteSqlRawAsync(sql, ct);
        }
    }

    private static bool IsInteger(Type type) => type == typeof(int) || type == typeof(long) || type == typeof(short);

    private static string QuoteTable(string? schema, string table) =>
        schema is null ? QuoteIdentifier(table) : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";

    private static string QuoteIdentifier(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string EscapeLiteral(string value) => value.Replace("'", "''");
}
