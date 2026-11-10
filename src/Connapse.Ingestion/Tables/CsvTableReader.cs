using System.Runtime.CompilerServices;
using System.Text;
using Connapse.Core;
using Connapse.Core.Utilities;
using Connapse.Ingestion.Parsers;
using Sylvan.Data.Csv;

namespace Connapse.Ingestion.Tables;

/// <summary>
/// Turns a CSV file into a typed table load. The file is read several times, never held in memory: once for its
/// layout, once to check every row against the column types, and once more as the store copies the rows in. A row
/// with any cell that doesn't fit its column is left out and listed, rather than dropped without a word.
/// </summary>
internal static class CsvTableReader
{
    /// <summary>How many rejects a load keeps and lists. Past this the types are guessed again from the whole file.</summary>
    public const int MaxRejects = 1000;

    /// <summary>How many rows at the start of the file always go into the type sample.</summary>
    public const int SampleHeadRows = 2048;

    /// <summary>How long a rejected value is kept in the list.</summary>
    public const int MaxRejectValueChars = 200;

    private const int PrefixBytes = 1024 * 1024;

    /// <summary>
    /// Reads the file's layout and checks every row. <paramref name="file"/> must be seekable and stay open until the
    /// returned load's rows have been read.
    /// </summary>
    /// <exception cref="InvalidDataException">The file has no rows, or too many rows of the wrong width to list.</exception>
    public static async Task<TableLoad> ReadAsync(Stream file, string fileName, Guid? documentId, CancellationToken ct = default)
    {
        if (!file.CanSeek)
            throw new ArgumentException("The CSV must be seekable; it is read more than once.", nameof(file));

        (Encoding encoding, string prefix, bool wholeFile) = await ReadPrefixAsync(file, ct);
        char? hint = Path.GetExtension(fileName).Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : null;
        char delimiter = CsvSniffer.DetectDelimiter(prefix, wholeFile, hint);

        // Pass 1: the first record, the usual width, and a sample spread over the whole file.
        string?[]? first = null;
        var widths = new Dictionary<int, long>();
        var sample = new SpreadSample(SampleHeadRows, CsvSniffer.SampleRows);
        foreach ((long _, string?[] record) in Records(file, encoding, delimiter, ct))
        {
            if (first is null)
                first = record;
            else
                sample.Add(record);
            widths[record.Length] = widths.GetValueOrDefault(record.Length) + 1;
        }
        if (first is null)
            throw new InvalidDataException("The file has no rows.");

        int width = widths.OrderByDescending(w => w.Value).ThenByDescending(w => w.Key).First().Key;
        IReadOnlyList<ValueReader> readers = CsvSniffer.InferTypes(sample.Rows, width);
        bool header = CsvSniffer.IsHeader(first, readers);
        IReadOnlyList<string> names = CsvSniffer.Names(header ? first : null, width);

        // Pass 2: every row against the types. Too many rejects means the sample missed something, so the types
        // are guessed again from every row; that pass reads each value, so after it only wrong-width rows fail.
        Validation check = Validate(file, encoding, delimiter, header, width, readers, ct);
        if (check.Count > MaxRejects)
        {
            readers = CsvSniffer.InferTypes(Records(file, encoding, delimiter, ct).Skip(header ? 1 : 0).Select(r => r.Fields), width);
            check = Validate(file, encoding, delimiter, header, width, readers, ct);
            if (check.Count > MaxRejects)
                throw new InvalidDataException(
                    $"{check.Count} rows don't have {width} fields; the file's rows are too uneven to load as a table.");
        }

        bool headerFits = !header || first.Length == width;
        string name = Path.GetFileNameWithoutExtension(fileName).Trim();
        if (name.Length == 0)
            name = "table";
        if (name.Length > 256)
            name = name[..256];

        TableColumn[] columns = [.. names.Select((n, i) => new TableColumn(n, readers[i].Type, Format: readers[i].Format))];
        return new TableLoad(
            name, columns, RowsAsync(file, encoding, delimiter, header, width, readers, ct), documentId,
            Description: null, NeedsReview: check.Count > 0 || !headerFits, Rejects: check.Rejects);
    }

    private sealed record Validation(long Count, IReadOnlyList<TableReject> Rejects);

    private static Validation Validate(
        Stream file, Encoding encoding, char delimiter, bool header, int width, IReadOnlyList<ValueReader> readers, CancellationToken ct)
    {
        List<TableReject> rejects = [];
        long count = 0;
        foreach ((long row, string?[] record) in Records(file, encoding, delimiter, ct))
        {
            if (header && row == 1)
                continue;
            List<TableReject>? found = Check(row, record, width, readers);
            if (found is null)
                continue;
            count++;
            foreach (TableReject reject in found)
            {
                if (rejects.Count < MaxRejects)
                    rejects.Add(reject);
            }
        }
        return new Validation(count, rejects);
    }

    /// <summary>Why a row can't load, one entry per failing cell, or one for the whole row if it has the wrong width; null when it loads.</summary>
    internal static List<TableReject>? Check(long row, string?[] record, int width, IReadOnlyList<ValueReader> readers)
    {
        if (record.Length != width)
            return [new TableReject(row, 0, Clip(string.Join(",", record)), $"has {record.Length} fields, expected {width}")];

        List<TableReject>? rejects = null;
        for (int c = 0; c < width; c++)
        {
            string? cell = record[c];
            if (CsvSniffer.IsNull(cell) || readers[c].TryRead(cell!, out _))
                continue;
            (rejects ??= []).Add(new TableReject(row, c + 1, Clip(cell!), $"not a {Describe(readers[c])}"));
        }
        return rejects;
    }

    private static string Describe(ValueReader reader) => reader.Type switch
    {
        TableColumnType.WholeNumber => "whole number",
        TableColumnType.Number => reader.Format?.DecimalChar is "," ? "number with a comma decimal" : "number",
        TableColumnType.Boolean => "true/false value",
        TableColumnType.Date => $"date in the form {reader.Format?.Pattern}",
        TableColumnType.DateTime => $"date and time in the form {reader.Format?.Pattern}",
        _ => reader.Type.ToString(),
    };

    private static string Clip(string value) =>
        StorableText.Clean(value.Length > MaxRejectValueChars ? value[..MaxRejectValueChars] : value);

    private static async IAsyncEnumerable<IReadOnlyList<object?>> RowsAsync(
        Stream file, Encoding encoding, char delimiter, bool header, int width, IReadOnlyList<ValueReader> readers,
        [EnumeratorCancellation] CancellationToken ct)
    {
        file.Position = 0;
        using var text = new StreamReader(file, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
        await using CsvDataReader reader = await CsvDataReader.CreateAsync(text, CsvSniffer.Options(delimiter), ct);
        long row = 0;
        while (await reader.ReadAsync(ct))
        {
            row++;
            if ((header && row == 1) || reader.RowFieldCount != width)
                continue;
            var values = new object?[width];
            bool fits = true;
            for (int c = 0; c < width && fits; c++)
            {
                string cell = reader.GetString(c);
                if (CsvSniffer.IsNull(cell))
                    values[c] = null;
                else if (readers[c].TryRead(cell, out object? value))
                    values[c] = value is string s ? StorableText.Clean(s) : value;
                else
                    fits = false;
            }
            if (fits)
                yield return values;
        }
    }

    private static IEnumerable<(long Row, string?[] Fields)> Records(Stream file, Encoding encoding, char delimiter, CancellationToken ct)
    {
        file.Position = 0;
        using var text = new StreamReader(file, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
        long row = 0;
        foreach (string?[] record in CsvSniffer.Records(text, delimiter))
        {
            if ((++row & 0xFFF) == 0)
                ct.ThrowIfCancellationRequested();
            yield return (row, record);
        }
    }

    /// <summary>The file's encoding, judged from its start, and that start as text for delimiter detection.</summary>
    private static async Task<(Encoding Encoding, string Prefix, bool WholeFile)> ReadPrefixAsync(Stream file, CancellationToken ct)
    {
        file.Position = 0;
        byte[] buffer = new byte[PrefixBytes];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await file.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
                break;
            read += n;
        }
        bool whole = read < buffer.Length || file.Position >= file.Length;
        int usable = whole ? read : Utf8Boundary(buffer.AsSpan(0, read));
        Encoding encoding = TextDecoding.Detect(buffer.AsSpan(0, usable), out int preamble);
        string prefix = encoding.GetString(buffer, preamble, usable - preamble);
        return (encoding, prefix, whole);
    }

    /// <summary>Where the bytes end without cutting a UTF-8 character, so a cut prefix doesn't fail strict UTF-8.</summary>
    internal static int Utf8Boundary(ReadOnlySpan<byte> bytes)
    {
        for (int back = 1; back <= 4 && back <= bytes.Length; back++)
        {
            byte b = bytes[^back];
            if ((b & 0xC0) == 0x80)
                continue; // a continuation byte; keep looking for its lead
            int needed = b < 0x80 ? 1 : b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
            return needed > back ? bytes.Length - back : bytes.Length;
        }
        return bytes.Length;
    }

    /// <summary>
    /// The first rows of the file plus rows spread evenly over the rest, in one pass: when the spread half fills, every
    /// other row in it is dropped and the stride doubles.
    /// </summary>
    private sealed class SpreadSample(int head, int max)
    {
        private readonly List<string?[]> _head = [];
        private List<string?[]> _spread = [];
        private long _seen;
        private long _stride = 1;

        public void Add(string?[] row)
        {
            if (_head.Count < head)
            {
                _head.Add(row);
                return;
            }
            if (_seen++ % _stride != 0)
                return;
            _spread.Add(row);
            if (_spread.Count >= max - head)
            {
                _spread = [.. _spread.Where((_, i) => i % 2 == 0)];
                _stride *= 2;
            }
        }

        public IEnumerable<string?[]> Rows => _head.Concat(_spread);
    }
}
