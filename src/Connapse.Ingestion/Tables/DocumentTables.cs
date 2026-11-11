using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Connapse.Core;
using Connapse.Core.Utilities;

namespace Connapse.Ingestion.Tables;

/// <summary>
/// A Markdown table found in a document's parsed text (CON-312). <see cref="Number"/> counts the document's tables from
/// 1 in reading order, gated or not, and <see cref="Page"/> is the PDF page, slide or sheet it sits on, or null for a
/// format without pages. <see cref="Start"/> and <see cref="Length"/> place it in the parsed text.
/// </summary>
internal sealed record FoundTable(
    int Number,
    int? Page,
    int Start,
    int Length,
    string Text,
    IReadOnlyList<string> Header,
    IReadOnlyList<string?[]> Rows)
{
    /// <summary>
    /// A hash of the header's cells, so a re-ingest can tell the same table from another one that took its place.
    /// </summary>
    public string HeaderHash { get; } = Hash(Header);

    /// <summary>The table's text lines, header and separator included, with surrounding spaces trimmed.</summary>
    public IReadOnlyList<string> Lines { get; } = [.. Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)];

    private static string Hash(IReadOnlyList<string> header)
    {
        string joined = string.Join("\u001F", header.Select(h => h.Trim().ToLowerInvariant()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..16].ToLowerInvariant();
    }
}

/// <summary>
/// Finds the Markdown pipe tables every document parser writes (PDF, Word, PowerPoint, Excel, HTML, EPUB, email,
/// Markdown) and decides which are clean enough to load as a table. Tables inside fenced code blocks are left alone.
/// </summary>
internal static partial class DocumentTables
{
    /// <summary>Fewer columns than this is a list, not a table.</summary>
    public const int MinColumns = 2;

    /// <summary>
    /// Fewer data rows than this is a layout box or a one-line callout. Docling drops 1x1 Word tables for the same
    /// reason (docs/research/con-312-table-extraction-and-passage-markers-2026-11-11.md).
    /// </summary>
    public const int MinRows = 2;

    /// <summary>The share of a table's data cells that must hold a value.</summary>
    public const double MinFilled = 0.5;

    /// <summary>The longest table name, matching the catalog's limit for loaded files.</summary>
    private const int MaxName = 256;

    [GeneratedRegex(@"^--- Page (\d+) ---$")]
    private static partial Regex PdfPage();

    [GeneratedRegex(@"^## Slide (\d+)\b")]
    private static partial Regex Slide();

    [GeneratedRegex(@"^## Sheet (\d+)\b")]
    private static partial Regex Sheet();

    /// <summary>Every table in <paramref name="content"/>, in reading order, with its page for paged formats.</summary>
    public static IReadOnlyList<FoundTable> Find(string content, string? extension)
    {
        Regex? pageMarker = (extension ?? "").ToLowerInvariant() switch
        {
            ".pdf" => PdfPage(),
            ".pptx" => Slide(),
            ".xlsx" => Sheet(),
            _ => null,
        };

        List<(int Start, string Text)> lines = SplitLines(content);
        List<FoundTable> found = [];
        int? page = null;
        bool inFence = false;
        int i = 0;
        while (i < lines.Count)
        {
            string trimmed = lines[i].Text.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                i++;
                continue;
            }
            if (inFence)
            {
                i++;
                continue;
            }
            if (pageMarker?.Match(trimmed) is { Success: true } m && int.TryParse(m.Groups[1].Value, out int p))
            {
                page = p;
                i++;
                continue;
            }
            if (!IsTableStart(lines, i))
            {
                i++;
                continue;
            }

            int end = i + 2;
            while (end < lines.Count && IsRow(lines[end].Text))
                end++;
            int start = lines[i].Start;
            int stop = lines[end - 1].Start + lines[end - 1].Text.Length;
            List<string?[]> rows = [.. lines.Skip(i + 2).Take(end - i - 2).Select(l => Cells(l.Text))];
            string?[] header = Cells(lines[i].Text);
            found.Add(new FoundTable(
                found.Count + 1, pageMarker is null ? null : page, start, stop - start, content[start..stop],
                [.. header.Select(h => h ?? "")], rows));
            i = end;
        }
        return found;
    }

    /// <summary>Why <paramref name="table"/> isn't clean enough to load, or null when it is.</summary>
    public static string? Reject(FoundTable table)
    {
        int width = table.Header.Count;
        if (width < MinColumns)
            return $"has {width} column; a table needs at least {MinColumns}";
        if (table.Rows.Count < MinRows)
            return $"has {table.Rows.Count} data row; a table needs at least {MinRows}";
        if (table.Header.All(string.IsNullOrWhiteSpace))
            return "has no column headers";
        if (table.Rows.FirstOrDefault(r => r.Length != width) is { } uneven)
            return $"has a row of {uneven.Length} cells under {width} headers";
        long cells = (long)width * table.Rows.Count;
        long filled = table.Rows.Sum(r => r.Count(c => !CsvSniffer.IsNull(c)));
        if (filled < cells * MinFilled)
            return $"has {filled} of {cells} cells filled";
        return null;
    }

    /// <summary>
    /// The name a promoted table gets: the document's name, then its page and number, such as
    /// <c>report, page 3, table 2</c>.
    /// </summary>
    public static string Name(string documentName, FoundTable table)
    {
        string stem = Path.GetFileNameWithoutExtension(documentName).Trim();
        if (stem.Length == 0)
            stem = "document";
        string suffix = table.Page is { } page ? $", page {page}, table {table.Number}" : $", table {table.Number}";
        if (stem.Length + suffix.Length > MaxName)
            stem = stem[..(MaxName - suffix.Length)];
        return StorableText.Clean(stem + suffix);
    }

    /// <summary>What to load for a clean table: its header as column names and each column's type guessed from every row.</summary>
    public static TableLoad ToLoad(FoundTable table, string name, Guid documentId, string? description = null)
    {
        int width = table.Header.Count;
        IReadOnlyList<ValueReader> readers = CsvSniffer.InferTypes(table.Rows, width);
        IReadOnlyList<string> names = CsvSniffer.Names([.. table.Header], width);
        TableColumn[] columns = [.. names.Select((n, c) => new TableColumn(n, readers[c].Type, Format: readers[c].Format))];
        return new TableLoad(name, columns, RowsAsync(table.Rows, readers), documentId, description);
    }

    private static async IAsyncEnumerable<IReadOnlyList<object?>> RowsAsync(
        IReadOnlyList<string?[]> rows, IReadOnlyList<ValueReader> readers, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (string?[] row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var values = new object?[readers.Count];
            for (int c = 0; c < readers.Count; c++)
            {
                string? cell = c < row.Length ? row[c] : null;
                if (CsvSniffer.IsNull(cell))
                    values[c] = null;
                else if (readers[c].TryRead(cell!, out object? value))
                    values[c] = value is string s ? StorableText.Clean(s) : value;
                else
                    values[c] = StorableText.Clean(cell!);
            }
            yield return values;
        }
        await Task.CompletedTask;
    }

    /// <summary>A row's cells, unescaping <c>\|</c>; empty cells are null.</summary>
    internal static string?[] Cells(string line)
    {
        string s = line.Trim();
        if (s.StartsWith('|'))
            s = s[1..];
        if (s.EndsWith('|') && !s.EndsWith("\\|", StringComparison.Ordinal))
            s = s[..^1];

        List<string?> cells = [];
        var cell = new StringBuilder();
        for (int k = 0; k < s.Length; k++)
        {
            if (s[k] == '\\' && k + 1 < s.Length && s[k + 1] == '|')
            {
                cell.Append('|');
                k++;
            }
            else if (s[k] == '|')
            {
                cells.Add(Value(cell));
                cell.Clear();
            }
            else
                cell.Append(s[k]);
        }
        cells.Add(Value(cell));
        return [.. cells];

        static string? Value(StringBuilder b)
        {
            string v = b.ToString().Trim();
            return v.Length == 0 ? null : v;
        }
    }

    private static bool IsTableStart(List<(int Start, string Text)> lines, int i) =>
        i + 1 < lines.Count && IsRow(lines[i].Text) && IsSeparator(lines[i + 1].Text);

    private static bool IsRow(string line) => line.TrimStart().StartsWith('|');

    private static bool IsSeparator(string line)
    {
        string trimmed = line.Trim();
        return trimmed.StartsWith('|') && trimmed.Contains("---") && trimmed.All(c => c is '|' or '-' or ':' or ' ');
    }

    private static List<(int Start, string Text)> SplitLines(string text)
    {
        var lines = new List<(int Start, string Text)>();
        int start = 0;
        while (start <= text.Length)
        {
            int newline = text.IndexOf('\n', start);
            int end = newline < 0 ? text.Length : newline;
            lines.Add((start, text[start..end].TrimEnd('\r')));
            if (newline < 0) break;
            start = newline + 1;
        }
        return lines;
    }
}
