using System.Text;

namespace EfCore.JsonSeeding;

/// <summary>
/// Разбирает CSV в строки таблицы; дальше их обрабатывает <see cref="TableSeedReader"/>.
/// </summary>
/// <remarks>
/// Рассчитано на файлы из Excel: разделитель <c>;</c>, <c>,</c> или табуляция определяется по заголовку,
/// кодировка — UTF-8 (с BOM или без) или Windows-1251.
/// </remarks>
internal static class CsvSeedReader
{
    private static readonly char[] Delimiters = [';', '\t', ','];

    public static List<TableRow> Read(byte[] bytes)
    {
        var text = Decode(bytes);
        return Parse(text, DetectDelimiter(text));
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
    private static List<TableRow> Parse(string text, char delimiter)
    {
        var rows = new List<TableRow>();
        var cells = new List<TableCell>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var rowLine = 1;

        void EndCell()
        {
            cells.Add(new TableCell(cell.ToString()));
            cell.Clear();
        }

        void EndRow()
        {
            EndCell();
            rows.Add(new TableRow(cells, rowLine));
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
}
