using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfCore.JsonSeeding;

/// <summary>
/// Ячейка таблицы. <see cref="IsNumber"/> — число из Excel (в том числе дата, которую Excel хранит числом).
/// </summary>
internal readonly record struct TableCell(string Text, bool IsNumber = false);

/// <summary>
/// Строка таблицы; <see cref="Line"/> — номер строки в файле или на листе Excel, для сообщений об ошибках.
/// </summary>
internal sealed record TableRow(IReadOnlyList<TableCell> Cells, int Line);

/// <summary>
/// Общая часть для CSV и Excel: первая строка — названия столбцов, остальные превращаются в такие же
/// записи, как в JSON. Дальше записи проходят те же проверки, что и JSON.
/// </summary>
internal static class TableSeedReader
{
    public static List<(JsonElement Element, string Where)> Build(IReadOnlyList<TableRow> rows, string origin, IEntityType type)
    {
        var records = new List<(JsonElement, string)>();
        if (rows.Count == 0)
            return records;

        var headers = ReadHeader(rows[0], origin);
        var properties = headers.Select(h => h.Length == 0 ? null : JsonSeeder.FindProperty(type, h)).ToArray();

        foreach (var row in rows.Skip(1))
        {
            if (row.Cells.All(c => string.IsNullOrWhiteSpace(c.Text)))
                continue;   // пустые строки, в том числе «;;;» из Excel

            var where = $"{origin}, строка {row.Line}";
            var record = new JsonObject();

            for (var i = 0; i < row.Cells.Count; i++)
            {
                var cell = row.Cells[i];
                if (i >= headers.Length || headers[i].Length == 0)
                {
                    if (cell.Text.Trim().Length == 0)
                        continue;
                    throw new JsonSeedingException(
                        $"{where}: значение «{cell.Text}» в столбце №{i + 1}, у которого нет названия в первой строке.");
                }

                if (cell.Text.Length == 0)
                    continue;   // пустая ячейка = значение по умолчанию

                var value = ConvertCell(cell, properties[i], headers[i], where);
                if (value is not null)
                    record[headers[i]] = value;
            }

            records.Add((JsonSerializer.SerializeToElement(record), where));
        }

        return records;
    }

    private static string[] ReadHeader(TableRow row, string origin)
    {
        var headers = row.Cells.Select(h => h.Text.Trim()).ToArray();
        var duplicate = headers
            .Where(h => h.Length > 0)
            .GroupBy(h => h, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new JsonSeedingException($"{origin}, строка {row.Line}: столбец «{duplicate.Key}» указан дважды.");
        return headers;
    }

    /// <summary>
    /// Значение ячейки по типу свойства. Столбцы, которых нет в сущности (например, <c>password</c>
    /// для Transform), остаются строками.
    /// </summary>
    private static JsonNode? ConvertCell(TableCell cell, IProperty? property, string column, string where)
    {
        var type = property is null ? typeof(string) : Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        if (type == typeof(string))
            return JsonValue.Create(cell.Text);

        var value = cell.Text.Trim();
        if (value.Length == 0)
            return null;

        if (IsInteger(type))
        {
            if (long.TryParse(RemoveSpaces(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                return JsonValue.Create(number);
            // Excel иногда хранит целое как 3.0000000000000004
            if (cell.IsNumber && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) &&
                Math.Abs(real - Math.Round(real)) < 1e-9)
                return JsonValue.Create((long)Math.Round(real));
            throw Invalid(where, column, cell.Text, "целое число");
        }

        if (type == typeof(decimal) || type == typeof(double) || type == typeof(float))
        {
            var text = cell.IsNumber ? value : NormalizeDecimal(value);
            if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return JsonValue.Create(number);
            throw Invalid(where, column, cell.Text, "число");
        }

        if (type == typeof(bool))
        {
            return value.ToLowerInvariant() switch
            {
                "true" or "1" or "да" or "yes" or "истина" => JsonValue.Create(true),
                "false" or "0" or "нет" or "no" or "ложь" => JsonValue.Create(false),
                _ => throw Invalid(where, column, cell.Text, "true/false, 1/0 или да/нет")
            };
        }

        // Дату Excel хранит числом дней с 1900 года
        if (cell.IsNumber && (type == typeof(DateTime) || type == typeof(DateOnly) || type == typeof(DateTimeOffset)) &&
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var oaDate))
        {
            var date = DateTime.FromOADate(oaDate);
            return type == typeof(DateOnly)
                ? JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                : JsonValue.Create(date.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        }

        if (type.IsEnum && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var enumNumber))
            return JsonValue.Create(enumNumber);

        // enum по имени, даты текстом (2026-11-10), Guid и прочее — строкой, как в JSON
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
