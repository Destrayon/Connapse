using System.Runtime.CompilerServices;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Utilities;

namespace Connapse.Ingestion.Tables;

/// <summary>
/// Turns a JSON file holding an array of flat objects into a typed table load (CON-308). Each object is a row and each
/// key a column, in the order keys first appear; a key an object lacks is an empty cell. The file is read twice, never
/// held whole: once to type every column, once as the store copies the rows in. Numbers keep their JSON type; a column
/// of strings is typed by the CSV loader's rules, so dates written as text load as dates.
/// <para>
/// Any other JSON isn't a table: a file whose top level isn't an array stays text only, without a word, and an array
/// holding anything but flat objects stays text only with a warning, since nested JSON doesn't load as a table yet.
/// </para>
/// </summary>
internal static class JsonTableReader
{
    /// <summary>The most columns a JSON table may have, as many as PostgreSQL allows less a margin.</summary>
    public const int MaxColumns = 1500;

    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 64 };

    /// <summary>The table, or null with the reason (null when no warning is due) when the file isn't a flat array.</summary>
    public static async Task<(TableLoad? Table, string? Warning)> ReadAsync(
        Stream file, string fileName, Guid? documentId, CancellationToken ct = default)
    {
        if (!file.CanSeek)
            throw new ArgumentException("The JSON must be seekable; it is read more than once.", nameof(file));
        if (!await StartsWithArrayAsync(file, ct))
            return (null, null);

        List<string> keys = [];
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        List<ColumnKinds> kinds = [];
        long rows = 0;

        file.Position = 0;
        await foreach (JsonElement item in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(file, Options, ct))
        {
            rows++;
            if (item.ValueKind != JsonValueKind.Object)
                return (null, $"{Path.GetFileName(fileName)} holds an array of {Describe(item.ValueKind)}, not of objects, so it wasn't loaded as a table");
            foreach (JsonProperty property in item.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    return (null, $"{Path.GetFileName(fileName)} has nested objects or arrays (in \"{Clip(property.Name)}\"); only flat JSON arrays load as tables for now");
                if (!index.TryGetValue(property.Name, out int c))
                {
                    if (keys.Count >= MaxColumns)
                        throw new InvalidDataException($"The JSON has more than {MaxColumns} different keys; too many columns for a table.");
                    c = keys.Count;
                    index[property.Name] = c;
                    keys.Add(property.Name);
                    kinds.Add(new ColumnKinds());
                }
                kinds[c].Add(property.Value);
            }
        }
        if (rows == 0 || keys.Count == 0)
            return (null, null);

        IReadOnlyList<string> names = CsvSniffer.Names([.. keys], keys.Count);
        TableColumnType[] types = [.. kinds.Select(k => k.Type)];
        TableColumn[] columns = [.. names.Select((n, i) => new TableColumn(n, types[i], Format: kinds[i].Reader?.Format))];
        string name = Path.GetFileNameWithoutExtension(fileName).Trim();
        if (name.Length == 0)
            name = "table";
        if (name.Length > XlsxTableReader.MaxNameChars)
            name = name[..XlsxTableReader.MaxNameChars];

        return (new TableLoad(name, columns, RowsAsync(file, index, kinds, ct), documentId), null);
    }

    private static async IAsyncEnumerable<IReadOnlyList<object?>> RowsAsync(
        Stream file, Dictionary<string, int> index, List<ColumnKinds> kinds, [EnumeratorCancellation] CancellationToken ct)
    {
        file.Position = 0;
        await foreach (JsonElement item in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(file, Options, ct))
        {
            var values = new object?[kinds.Count];
            foreach (JsonProperty property in item.EnumerateObject())
            {
                int c = index[property.Name];
                values[c] = kinds[c].Convert(property.Value);
            }
            yield return values;
        }
    }

    /// <summary>Whether the first thing in the file, after a byte order mark and white space, is '['.</summary>
    private static async Task<bool> StartsWithArrayAsync(Stream file, CancellationToken ct)
    {
        file.Position = 0;
        byte[] buffer = new byte[4096];
        int read = await file.ReadAsync(buffer, ct);
        int i = read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
        while (i < read && buffer[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            i++;
        return i < read && buffer[i] == (byte)'[';
    }

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array => "arrays",
        JsonValueKind.String => "strings",
        JsonValueKind.Number => "numbers",
        JsonValueKind.True or JsonValueKind.False => "true/false values",
        _ => "nulls",
    };

    private static string Clip(string text) => StorableText.Clean(text.Length > 64 ? text[..64] : text);

    /// <summary>The kinds of value one key holds, and from them its column type.</summary>
    private sealed class ColumnKinds
    {
        private bool _strings, _numbers, _booleans, _fraction, _huge;
        private bool[]? _alive;

        /// <summary>The CSV reader a column of strings is read with; null for any other column.</summary>
        public ValueReader? Reader { get; private set; }

        public void Add(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    _strings = true;
                    string text = value.GetString()!;
                    if (CsvSniffer.IsNull(text))
                        break;
                    IReadOnlyList<ValueReader> candidates = ValueReaders.Candidates;
                    _alive ??= [.. Enumerable.Repeat(true, candidates.Count)];
                    for (int k = 0; k < candidates.Count - 1; k++)
                    {
                        if (_alive[k] && !candidates[k].TryRead(text, out _))
                            _alive[k] = false;
                    }
                    break;
                case JsonValueKind.Number:
                    _numbers = true;
                    if (!value.TryGetInt64(out _))
                    {
                        _fraction = true;
                        _huge |= !value.TryGetDecimal(out _);
                    }
                    break;
                case JsonValueKind.True or JsonValueKind.False:
                    _booleans = true;
                    break;
            }
        }

        public TableColumnType Type
        {
            get
            {
                Reader = null;
                switch (_strings, _numbers, _booleans)
                {
                    case (false, true, false):
                        return _huge ? TableColumnType.Text : _fraction ? TableColumnType.Number : TableColumnType.WholeNumber;
                    case (false, false, true):
                        return TableColumnType.Boolean;
                    case (true, false, false):
                        Reader = _alive is null ? ValueReaders.Text : ValueReaders.Candidates[Array.IndexOf(_alive, true)];
                        return Reader.Type;
                    default:
                        return TableColumnType.Text;
                }
            }
        }

        public object? Convert(JsonElement value)
        {
            TableColumnType type = Type;
            switch (value.ValueKind)
            {
                case JsonValueKind.Null or JsonValueKind.Undefined:
                    return null;
                case JsonValueKind.String:
                    string text = value.GetString()!;
                    if (Reader is null)
                        return StorableText.Clean(text);
                    if (CsvSniffer.IsNull(text))
                        return null;
                    Reader.TryRead(text, out object? read);
                    return read is string s ? StorableText.Clean(s) : read;
                case JsonValueKind.Number when type == TableColumnType.WholeNumber:
                    return value.GetInt64();
                case JsonValueKind.Number when type == TableColumnType.Number:
                    return value.GetDecimal();
                case JsonValueKind.True or JsonValueKind.False when type == TableColumnType.Boolean:
                    return value.GetBoolean();
                default:
                    return StorableText.Clean(value.ValueKind switch
                    {
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        _ => value.GetRawText(),
                    });
            }
        }
    }
}
