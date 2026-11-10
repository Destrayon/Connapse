using Connapse.Core;
using Connapse.Core.Utilities;
using Sylvan.Data.Csv;

namespace Connapse.Ingestion.Tables;

/// <summary>
/// Guesses a CSV's layout the way DuckDB's sniffer does: the delimiter that splits rows into the same number of
/// columns, each column's type from a sample of rows, and whether the first row is a header.
/// Source: https://duckdb.org/docs/2.0/data/csv/auto_detection.html.
/// </summary>
internal static class CsvSniffer
{
    /// <summary>DuckDB's delimiter candidates.</summary>
    public static readonly char[] Delimiters = [',', '|', ';', '\t'];

    /// <summary>DuckDB's default sample size, in rows.</summary>
    public const int SampleRows = 20_480;

    /// <summary>The longest single field read; a bigger one fails the load rather than exhausting memory.</summary>
    public const int MaxFieldChars = 16 * 1024 * 1024;

    /// <summary>The longest column name kept; a longer header cell is cut.</summary>
    public const int MaxColumnName = 256;

    /// <summary>
    /// The delimiter that splits the most rows into the same number of columns. A delimiter that splits rows into
    /// more than one column beats one that never appears; between those, the one with the most consistent rows wins,
    /// then the one with more columns, then the earlier candidate. <paramref name="preferred"/> (a tab for .tsv) is
    /// tried first, so it wins a tie.
    /// </summary>
    /// <param name="sample">The start of the file.</param>
    /// <param name="wholeFile">Whether the sample is the whole file; when not, its last row may be cut short and is ignored.</param>
    public static char DetectDelimiter(string sample, bool wholeFile, char? preferred = null)
    {
        IEnumerable<char> order = preferred is { } p ? [p, .. Delimiters.Where(d => d != p)] : Delimiters;
        char best = order.First();
        (bool Split, int Consistent, int Columns) bestScore = (false, -1, 0);
        foreach (char delimiter in order)
        {
            List<int> counts = [];
            try
            {
                using var reader = new StringReader(sample);
                foreach (string?[] record in Records(reader, delimiter))
                {
                    counts.Add(record.Length);
                    if (counts.Count > SampleRows)
                        break;
                }
            }
            catch (CsvFormatException) when (wholeFile)
            {
                // A quote this delimiter can't make sense of rules it out, as a quoting error rules out a DuckDB
                // dialect. In a cut sample the error may only be the cut, so the rows before it still count.
                continue;
            }
            catch (CsvFormatException)
            {
            }
            if (!wholeFile && counts.Count > 1)
                counts.RemoveAt(counts.Count - 1);
            if (counts.Count == 0)
                continue;

            var modal = counts.GroupBy(c => c).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First();
            (bool, int, int) score = (modal.Key > 1, modal.Count(), modal.Key);
            if (score.CompareTo(bestScore) > 0)
            {
                best = delimiter;
                bestScore = score;
            }
        }
        return best;
    }

    /// <summary>
    /// Each column's reader: the highest-priority candidate that reads every non-empty value the column has in
    /// <paramref name="rows"/>. Rows with another number of fields are skipped. A column with no values is text.
    /// </summary>
    public static IReadOnlyList<ValueReader> InferTypes(IEnumerable<string?[]> rows, int columns)
    {
        IReadOnlyList<ValueReader> candidates = ValueReaders.Candidates;
        bool[][] alive = [.. Enumerable.Range(0, columns).Select(_ => Enumerable.Repeat(true, candidates.Count).ToArray())];
        bool[] seen = new bool[columns];

        foreach (string?[] row in rows)
        {
            if (row.Length != columns)
                continue;
            for (int c = 0; c < columns; c++)
            {
                if (IsNull(row[c]))
                    continue;
                seen[c] = true;
                bool[] live = alive[c];
                for (int k = 0; k < candidates.Count - 1; k++)
                {
                    if (live[k] && !candidates[k].TryRead(row[c]!, out _))
                        live[k] = false;
                }
            }
        }

        var readers = new ValueReader[columns];
        for (int c = 0; c < columns; c++)
        {
            int first = seen[c] ? Array.IndexOf(alive[c], true) : -1;
            readers[c] = first < 0 ? ValueReaders.Text : candidates[first];
        }
        return readers;
    }

    /// <summary>
    /// DuckDB's header rule: the first row is a header when a value in it doesn't fit its column's type, and is
    /// assumed to be one when every column is text, since then nothing tells a header from data.
    /// </summary>
    public static bool IsHeader(string?[] first, IReadOnlyList<ValueReader> readers)
    {
        if (readers.All(r => r.Type == TableColumnType.Text))
            return true;
        for (int c = 0; c < readers.Count && c < first.Length; c++)
        {
            if (readers[c].Type != TableColumnType.Text && !IsNull(first[c]) && !readers[c].TryRead(first[c]!, out _))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Column names from the header, or DuckDB's column0, column1… without one. An empty header cell gets the
    /// generated name for its place, and a repeated name gets _1, _2… so every name is unique.
    /// </summary>
    public static IReadOnlyList<string> Names(string?[]? header, int columns)
    {
        var names = new string[columns];
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int c = 0; c < columns; c++)
        {
            string raw = header is not null && c < header.Length && !IsNull(header[c]) ? StorableText.Clean(header[c]!.Trim()) : "";
            if (raw.Length > MaxColumnName)
                raw = raw[..MaxColumnName];
            string name = raw.Length == 0 ? $"column{c}" : raw;
            string unique = name;
            for (int n = 1; !used.Add(unique); n++)
                unique = $"{name}_{n}";
            names[c] = unique;
        }
        return names;
    }

    public static bool IsNull(string? value) => string.IsNullOrWhiteSpace(value);

    /// <summary>Every record in the text, as its fields, with no header handling.</summary>
    public static IEnumerable<string?[]> Records(TextReader text, char delimiter)
    {
        using CsvDataReader reader = CsvDataReader.Create(text, Options(delimiter));
        while (reader.Read())
        {
            var fields = new string?[reader.RowFieldCount];
            for (int i = 0; i < fields.Length; i++)
                fields[i] = reader.GetString(i);
            yield return fields;
        }
    }

    public static CsvDataReaderOptions Options(char delimiter) => new()
    {
        HasHeaders = false,
        Delimiter = delimiter,
        Quote = '"',
        BufferSize = 64 * 1024,
        MaxBufferSize = MaxFieldChars,
    };
}
