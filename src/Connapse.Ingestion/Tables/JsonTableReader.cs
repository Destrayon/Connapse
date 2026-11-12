using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Utilities;

namespace Connapse.Ingestion.Tables;

/// <summary>
/// Turns a JSON or JSON Lines file into typed tables (CON-308, CON-335). An array of flat objects is one table: each
/// object a row and each key a column, in the order keys first appear, a key an object lacks an empty cell. Nested
/// objects and lists split into parent and child tables by <see cref="TreeFlattener"/>. A JSON Lines file is read the
/// same way as an array, one record per line; a line that isn't JSON is listed as a rejected row. A file whose top
/// level is one object loads only when it holds a list of two or more items, so a settings file stays text only.
/// <para>
/// An array or JSON Lines file is streamed, never held whole, and read more than once: to learn its shape, to type its
/// columns, and as the store copies each table's rows in. A top-level object is held in memory, up to
/// <see cref="MaxTreeBytes"/>. Numbers keep their JSON type; a column of strings is typed by the CSV loader's rules, so
/// dates written as text load as dates.
/// </para>
/// </summary>
internal static class JsonTableReader
{
    /// <summary>The most columns a JSON table may have, as many as PostgreSQL allows less a margin.</summary>
    public const int MaxColumns = 1500;

    /// <summary>The largest file read as one value in memory: a top-level JSON object, an XML or YAML document.</summary>
    public const long MaxTreeBytes = 64L * 1024 * 1024;

    /// <summary>The longest rejected line quoted in a reject.</summary>
    private const int MaxRejectChars = 200;

    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 64 };

    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 64 };

    /// <summary>
    /// The tables, or none with the reason (null when no warning is due) when the file isn't a table. <c>Owner</c> holds
    /// what the rows stream from, and must outlive the store copying them in.
    /// </summary>
    public static async Task<(IReadOnlyList<TableLoad> Tables, string? Warning, IDisposable? Owner)> ReadAsync(
        Stream file, string fileName, Guid? documentId, CancellationToken ct = default)
    {
        if (!file.CanSeek)
            throw new ArgumentException("The JSON must be seekable; it is read more than once.", nameof(file));
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is ".jsonl" or ".ndjson")
            return (await TreeFlattener.FlattenAsync(Lines(file), fileName, documentId, ct), null, null);

        byte first = await FirstByteAsync(file, ct);
        if (first == (byte)'[')
            return (await TreeFlattener.FlattenAsync(Array(file), fileName, documentId, ct), null, null);
        if (first != (byte)'{')
            return ([], null, null);

        if (await IsJsonLinesAsync(file, ct))
            return (await TreeFlattener.FlattenAsync(Lines(file), fileName, documentId, ct), null, null);
        if (file.Length > MaxTreeBytes)
            return ([], $"{Path.GetFileName(fileName)} is one JSON object larger than {MaxTreeBytes / (1024 * 1024)} MB, too large to load as tables", null);

        file.Position = 0;
        JsonDocument document = await JsonDocument.ParseAsync(file, DocumentOptions, ct);
        return await FlattenOwnedAsync(document, One(document.RootElement), fileName, documentId, ct);
    }

    /// <summary>
    /// The tables of a value held in <paramref name="document"/>, which becomes their owner, or none, with the document
    /// disposed, when the value isn't a table.
    /// </summary>
    public static async Task<(IReadOnlyList<TableLoad> Tables, string? Warning, IDisposable? Owner)> FlattenOwnedAsync(
        JsonDocument document, RecordSource source, string fileName, Guid? documentId, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<TableLoad> tables = await TreeFlattener.FlattenAsync(source, fileName, documentId, ct);
            if (tables.Count == 0)
            {
                document.Dispose();
                return ([], null, null);
            }
            return (tables, null, document);
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    /// <summary>The records of a file holding one value in memory, such as a parsed top-level object.</summary>
    public static RecordSource One(JsonElement record, string textKey = "value") =>
        new(_ => Single(record), singleRecord: true, textKey);

    private static async IAsyncEnumerable<JsonElement> Single(JsonElement record)
    {
        await Task.CompletedTask;
        yield return record;
    }

    /// <summary>The items of a top-level array, streamed.</summary>
    private static RecordSource Array(Stream file) => new(ct => ArrayItems(file, ct), singleRecord: false);

    private static async IAsyncEnumerable<JsonElement> ArrayItems(Stream file, [EnumeratorCancellation] CancellationToken ct)
    {
        file.Position = 0;
        await foreach (JsonElement item in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(file, Options, ct))
            yield return item;
    }

    /// <summary>One record per line; blank lines are skipped and a line that isn't JSON is a rejected row.</summary>
    private static RecordSource Lines(Stream file)
    {
        RecordSource? source = null;
        bool read = false;
        source = new RecordSource(ct => LineItems(file, source!, !read && (read = true), ct), singleRecord: false);
        return source;
    }

    private static async IAsyncEnumerable<JsonElement> LineItems(
        Stream file, RecordSource source, bool firstRead, [EnumeratorCancellation] CancellationToken ct)
    {
        file.Position = 0;
        using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        long lineNumber = 0;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line, DocumentOptions);
            }
            catch (JsonException e)
            {
                if (firstRead && source.Rejects.Count < CsvTableReader.MaxRejects)
                {
                    string value = StorableText.Clean(line.Length > MaxRejectChars ? line[..MaxRejectChars] : line);
                    source.Rejects.Add(new TableReject(lineNumber, 0, value, $"Not a JSON value: {e.Message}"));
                }
                continue;
            }
            using (document)
                yield return document.RootElement;
        }
    }

    /// <summary>
    /// Whether a .json file starting with '{' is really JSON Lines: its first line is a whole JSON value and more follows.
    /// </summary>
    private static async Task<bool> IsJsonLinesAsync(Stream file, CancellationToken ct)
    {
        file.Position = 0;
        using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        string? line = await reader.ReadLineAsync(ct);
        if (line is null)
            return false;
        try
        {
            using JsonDocument _ = JsonDocument.Parse(line, DocumentOptions);
        }
        catch (JsonException)
        {
            return false;
        }
        while (await reader.ReadLineAsync(ct) is { } next)
        {
            if (!string.IsNullOrWhiteSpace(next))
                return true;
        }
        return false;
    }

    /// <summary>The first byte of the file after a byte order mark and white space, or 0 when there is none.</summary>
    private static async Task<byte> FirstByteAsync(Stream file, CancellationToken ct)
    {
        file.Position = 0;
        byte[] buffer = new byte[4096];
        int read = await file.ReadAsync(buffer, ct);
        int i = read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
        while (i < read && buffer[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            i++;
        return i < read ? buffer[i] : (byte)0;
    }
}
