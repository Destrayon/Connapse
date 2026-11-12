using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Connapse.Core;
using Connapse.Core.Utilities;

namespace Connapse.Ingestion.Tables;

/// <summary>
/// The records of a structured file, as JSON values the flattener reads (CON-335). <see cref="Records"/> starts the
/// file over on each call, since the flattener reads it more than once. <see cref="SingleRecord"/> is true for a file
/// that is one value as a whole (a JSON object, an XML document, a YAML document), which loads only when it holds a
/// list of two or more items; a file of many records (a JSON array, JSON Lines) always loads. <see cref="TextKey"/>
/// names the column a plain value takes where other items at the same place are objects: <c>#text</c> for XML,
/// <c>value</c> otherwise. <see cref="Rejects"/> lists the records that couldn't be read, once the first read is done.
/// </summary>
internal sealed class RecordSource(
    Func<CancellationToken, IAsyncEnumerable<JsonElement>> records, bool singleRecord, string textKey = "value")
{
    public Func<CancellationToken, IAsyncEnumerable<JsonElement>> Records { get; } = records;

    public bool SingleRecord { get; } = singleRecord;

    public string TextKey { get; } = textKey;

    public List<TableReject> Rejects { get; } = [];
}

/// <summary>
/// Splits nested records into tables an agent can filter, count and join (CON-335), the way dlt normalizes JSON (see
/// docs/research/nested-structured-tables-2026-11-11.md). The records make one table, named after the file. A nested
/// object's fields become columns of the same table, named by their path (<c>customer.address.city</c>). A list
/// becomes a table of its own, named <c>&lt;table&gt;.&lt;path&gt;</c>, with a row per item: <c>_parent_row</c> holds
/// the <c>_row</c> of the row the list is in and <c>_index</c> the item's place in the list from 0, so the tables
/// join on those two columns. A list of plain values has one column, <c>value</c>.
/// <para>
/// Each decision is made once for the whole file, so every record splits the same way: a field that is a list in any
/// record is a list in all of them (a lone value is a list of one), a key repeated inside one object is a list (how
/// XML repeats an element), and a plain value where other records have an object goes in the object's
/// <see cref="RecordSource.TextKey"/> column. What can't be split sensibly stays whole as a JSON column (jsonb), never
/// dropped: a list of lists, an object with more than <see cref="MaxKeys"/> keys (DuckDB's limit for treating an
/// object as a map), and anything past the depth or table limits.
/// </para>
/// <para>
/// Ids are counted in file order, so each read of the file gives each item the same id, and the file is read once to
/// learn its shape, once to type its columns, and once per table as the store copies that table's rows in.
/// </para>
/// </summary>
internal static class TreeFlattener
{
    /// <summary>The most keys an object may have before it is kept whole as JSON instead of split into columns.</summary>
    public const int MaxKeys = 200;

    /// <summary>The most objects deep a column's path may run inside one table before the rest is kept as JSON.</summary>
    public const int MaxInlineDepth = 16;

    /// <summary>The most tables deep a list may sit before it is kept as JSON.</summary>
    public const int MaxTableDepth = 6;

    /// <summary>The most tables one file splits into; further lists are kept as JSON.</summary>
    public const int MaxTables = 50;

    /// <summary>How deep the shape of a record is followed; anything deeper is kept as JSON.</summary>
    public const int MaxShapeDepth = 48;

    public const string RowColumn = "_row";
    public const string ParentColumn = "_parent_row";
    public const string IndexColumn = "_index";

    /// <summary>The tables the records split into, in parent-before-child order; empty when they aren't a table.</summary>
    public static async Task<IReadOnlyList<TableLoad>> FlattenAsync(
        RecordSource source, string fileName, Guid? documentId, CancellationToken ct = default)
    {
        var shape = new Shape();
        long records = 0;
        await foreach (JsonElement record in source.Records(ct))
        {
            if (IsMissing(record))
                continue;
            records++;
            Observe(shape, record, 0);
        }
        if (records == 0)
            return [];
        Normalize(shape);

        List<TablePlan> plans = [];
        var root = new TablePlan(TableName(fileName), null, [], 1);
        Plan(root, shape, source.TextKey, plans);

        await foreach (JsonElement record in source.Records(ct))
        {
            if (!IsMissing(record))
                Visit(root, record);
        }

        if (source.SingleRecord && !plans.Any(p => p.Parent is not null && p.Rows >= 2))
            return [];
        foreach (TablePlan plan in plans)
            plan.Emitted = plan.Rows > 0 && (plan.Parent is not null || plan.Columns.Count > 0);
        foreach (TablePlan plan in plans)
        {
            plan.HasIndex = plan.Parent is not null;
            plan.HasParent = plan.Parent is { } parent && !(parent.Parent is null && parent.Rows == 1);
        }
        foreach (TablePlan plan in plans)
            plan.HasRow = plan.Emitted && plan.Children.Any(c => c.Emitted && c.HasParent);

        List<TablePlan> emitted = [.. plans.Where(p => p.Emitted)];
        AssignNames(emitted);
        string file = Path.GetFileName(fileName);
        return [.. emitted.Select(p => p.ToLoad(source, file, documentId, ct))];
    }

    private static bool IsMissing(JsonElement value) => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    private static string TableName(string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName).Trim();
        return name.Length == 0 ? "table" : name;
    }

    /// <summary>Clips each table's name to the catalog's limit and makes the names unique.</summary>
    private static void AssignNames(List<TablePlan> plans)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TablePlan plan in plans)
        {
            string name = StorableText.Clean(plan.Name);
            if (name.Length > XlsxTableReader.MaxNameChars)
                name = name[..XlsxTableReader.MaxNameChars];
            string unique = name;
            for (int n = 2; !used.Add(unique); n++)
            {
                string suffix = $"_{n}";
                unique = (name.Length + suffix.Length > XlsxTableReader.MaxNameChars
                    ? name[..(XlsxTableReader.MaxNameChars - suffix.Length)]
                    : name) + suffix;
            }
            plan.Name = unique;
        }
    }

    // ---- Shape: what each place in the records holds, over the whole file ----

    /// <summary>What the values at one place in the records hold: objects, lists, plain values, or a mix.</summary>
    private sealed class Shape
    {
        public bool Object, Array, Scalar, Json;
        public readonly List<string> Order = [];
        public readonly Dictionary<string, Shape> Keys = new(StringComparer.Ordinal);
        public Shape? Element;

        /// <summary>The shape of a key, or null when the object has too many keys to split.</summary>
        public Shape? Child(string key)
        {
            if (Keys.TryGetValue(key, out Shape? child))
                return child;
            if (Keys.Count >= MaxKeys)
                return null;
            child = new Shape();
            Keys[key] = child;
            Order.Add(key);
            return child;
        }

        public void MakeJson()
        {
            Json = true;
            Keys.Clear();
            Order.Clear();
            Element = null;
        }
    }

    private static void Observe(Shape shape, JsonElement value, int depth)
    {
        if (shape.Json)
            return;
        if (depth > MaxShapeDepth)
        {
            shape.MakeJson();
            return;
        }
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                shape.Object = true;
                HashSet<string>? seen = null;
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    Shape? child = shape.Child(property.Name);
                    if (child is null)
                    {
                        shape.MakeJson();
                        return;
                    }
                    seen ??= new HashSet<string>(StringComparer.Ordinal);
                    if (seen.Add(property.Name))
                        Observe(child, property.Value, depth + 1);
                    else
                        ObserveRepeat(child, property.Value, depth + 1);
                }
                break;
            case JsonValueKind.Array:
                shape.Array = true;
                shape.Element ??= new Shape();
                foreach (JsonElement item in value.EnumerateArray())
                    Observe(shape.Element, item, depth + 1);
                break;
            case JsonValueKind.Null or JsonValueKind.Undefined:
                break;
            default:
                shape.Scalar = true;
                break;
        }
    }

    /// <summary>A key seen again in the same object: its values are a list's items.</summary>
    private static void ObserveRepeat(Shape child, JsonElement value, int depth)
    {
        if (child.Json)
            return;
        child.Array = true;
        if (value.ValueKind == JsonValueKind.Array)
            Observe(child, value, depth);
        else if (!IsMissing(value))
        {
            child.Element ??= new Shape();
            Observe(child.Element, value, depth + 1);
        }
    }

    /// <summary>
    /// Makes a place that was a list anywhere a list everywhere: the objects and values seen there outside a list
    /// become items of it.
    /// </summary>
    private static void Normalize(Shape shape)
    {
        if (shape.Json)
            return;
        if (shape.Array)
        {
            shape.Element ??= new Shape();
            if (shape.Object || shape.Scalar)
            {
                var single = new Shape { Object = shape.Object, Scalar = shape.Scalar };
                foreach (string key in shape.Order)
                {
                    single.Order.Add(key);
                    single.Keys[key] = shape.Keys[key];
                }
                shape.Object = shape.Scalar = false;
                shape.Keys.Clear();
                shape.Order.Clear();
                Merge(shape.Element, single);
            }
            Normalize(shape.Element);
            return;
        }
        foreach (Shape child in shape.Keys.Values)
            Normalize(child);
    }

    private static void Merge(Shape into, Shape from)
    {
        if (into.Json)
            return;
        if (from.Json)
        {
            into.MakeJson();
            return;
        }
        into.Object |= from.Object;
        into.Array |= from.Array;
        into.Scalar |= from.Scalar;
        foreach (string key in from.Order)
        {
            if (into.Keys.TryGetValue(key, out Shape? existing))
                Merge(existing, from.Keys[key]);
            else if (into.Keys.Count >= MaxKeys)
            {
                into.MakeJson();
                return;
            }
            else
            {
                into.Keys[key] = from.Keys[key];
                into.Order.Add(key);
            }
        }
        if (from.Element is not null)
        {
            into.Element ??= new Shape();
            Merge(into.Element, from.Element);
        }
    }

    // ---- Plan: which tables and columns the shape becomes ----

    private enum ColumnMode
    {
        /// <summary>A plain value at the column's path.</summary>
        Scalar,

        /// <summary>A plain value at the path, or the text key of the object there.</summary>
        Wrapped,

        /// <summary>The value at the path, kept whole as JSON.</summary>
        Json,

        /// <summary>Every value of a list at the path, kept whole as one JSON array.</summary>
        JsonList,
    }

    private sealed class ColumnPlan(string[] keys, ColumnMode mode, string name, string? textKey = null)
    {
        public string[] Keys { get; } = keys;
        public ColumnMode Mode { get; } = mode;
        public string Name { get; } = name;
        public string? TextKey { get; } = textKey;
        public ValueKinds Kinds { get; } = new();
        public bool IsJson => Mode is ColumnMode.Json or ColumnMode.JsonList;
    }

    private sealed class TablePlan(string name, TablePlan? parent, string[] path, int depth)
    {
        public string Name { get; set; } = name;
        public TablePlan? Parent { get; } = parent;

        /// <summary>The keys, inside a row of the parent, of the list this table's rows are the items of.</summary>
        public string[] Path { get; } = path;

        public int Depth { get; } = depth;
        public List<ColumnPlan> Columns { get; } = [];
        public List<TablePlan> Children { get; } = [];
        public long Rows { get; set; }
        public bool Emitted { get; set; }
        public bool HasRow { get; set; }
        public bool HasParent { get; set; }
        public bool HasIndex { get; set; }

        private int KeyCount => (HasRow ? 1 : 0) + (HasParent ? 1 : 0) + (HasIndex ? 1 : 0);

        public TableLoad ToLoad(RecordSource source, string file, Guid? documentId, CancellationToken ct)
        {
            List<string> raw = [];
            List<TableColumn> keys = [];
            string list = string.Join('.', Path);
            if (HasRow)
            {
                string children = string.Join(", ", Children.Where(c => c.Emitted && c.HasParent).Select(c => c.Name));
                keys.Add(new TableColumn(RowColumn, TableColumnType.WholeNumber,
                    $"Identifies the row. Rows of {children} point to it with {ParentColumn}."));
            }
            if (HasParent)
            {
                keys.Add(new TableColumn(ParentColumn, TableColumnType.WholeNumber, Parent!.Emitted
                    ? $"The {RowColumn} of the {Parent.Name} row whose \"{list}\" list holds this item."
                    : $"Which record of {file} this item is in, counted from 1; items of the same record share it."));
            }
            if (HasIndex)
                keys.Add(new TableColumn(IndexColumn, TableColumnType.WholeNumber, $"The item's place in its \"{list}\" list, from 0."));
            raw.AddRange(keys.Select(k => k.Name));
            raw.AddRange(Columns.Select(c => c.Name));
            if (raw.Count > JsonTableReader.MaxColumns)
                throw new InvalidDataException($"{Name} would have more than {JsonTableReader.MaxColumns} columns; too many for a table.");

            IReadOnlyList<string> names = CsvSniffer.Names([.. raw], raw.Count);
            List<TableColumn> columns = [.. keys.Select((k, i) => k with { Name = names[i] })];
            for (int c = 0; c < Columns.Count; c++)
            {
                ColumnPlan column = Columns[c];
                string name = names[keys.Count + c];
                if (column.IsJson)
                    columns.Add(new TableColumn(name, TableColumnType.Json, "Nested data kept whole as JSON."));
                else
                    columns.Add(new TableColumn(name, column.Kinds.Type, Format: column.Kinds.Reader?.Format));
            }

            string? description = Parent is null ? null : Parent.Emitted
                ? $"One row per item of the \"{list}\" lists in {Parent.Name}; join {ParentColumn} to {Parent.Name}.{RowColumn}."
                : $"One row per item of the \"{list}\" lists in the records of {file}.";
            bool root = Parent is null;
            IReadOnlyList<TableReject>? rejects = root && source.Rejects.Count > 0 ? [.. source.Rejects] : null;
            return new TableLoad(Name, columns, RowsAsync(source, this, ct), documentId, description,
                NeedsReview: rejects is not null, Rejects: rejects);
        }

        public object?[] Row(JsonElement item, long id, long parentId, long index)
        {
            var values = new object?[KeyCount + Columns.Count];
            int k = 0;
            if (HasRow)
                values[k++] = id;
            if (HasParent)
                values[k++] = parentId;
            if (HasIndex)
                values[k++] = index;
            for (int c = 0; c < Columns.Count; c++)
                values[k + c] = Columns[c].Value(item);
            return values;
        }
    }

    private static void Plan(TablePlan table, Shape element, string textKey, List<TablePlan> plans)
    {
        plans.Add(table);
        if (element.Json || element.Array)
            table.Columns.Add(new ColumnPlan([], ColumnMode.Json, "value"));
        else if (element.Object)
        {
            if (element.Scalar)
                table.Columns.Add(new ColumnPlan([], ColumnMode.Wrapped, textKey, textKey));
            AddColumns(table, element, [], textKey, plans);
        }
        else
            table.Columns.Add(new ColumnPlan([], ColumnMode.Scalar, "value"));
    }

    private static void AddColumns(TablePlan table, Shape shape, string[] prefix, string textKey, List<TablePlan> plans)
    {
        foreach (string key in shape.Order)
        {
            Shape child = shape.Keys[key];
            string[] path = [.. prefix, key];
            string name = string.Join('.', path);
            if (shape.Scalar && key == textKey && !child.Object && !child.Array && !child.Json)
                continue; // the wrapped column already reads it
            if (child.Json)
                table.Columns.Add(new ColumnPlan(path, child.Array ? ColumnMode.JsonList : ColumnMode.Json, name));
            else if (child.Array)
            {
                if (table.Depth >= MaxTableDepth || plans.Count >= MaxTables)
                    table.Columns.Add(new ColumnPlan(path, ColumnMode.JsonList, name));
                else
                {
                    var list = new TablePlan($"{table.Name}.{name}", table, path, table.Depth + 1);
                    table.Children.Add(list);
                    Plan(list, child.Element!, textKey, plans);
                }
            }
            else if (child.Object)
            {
                if (path.Length >= MaxInlineDepth)
                    table.Columns.Add(new ColumnPlan(path, ColumnMode.Json, name));
                else
                {
                    if (child.Scalar)
                        table.Columns.Add(new ColumnPlan(path, ColumnMode.Wrapped, $"{name}.{textKey}", textKey));
                    AddColumns(table, child, path, textKey, plans);
                }
            }
            else
                table.Columns.Add(new ColumnPlan(path, ColumnMode.Scalar, name));
        }
    }

    // ---- Reading values out of the records ----

    /// <summary>Counts a table's rows and types its columns, and its child tables', from one of its items.</summary>
    private static void Visit(TablePlan table, JsonElement item)
    {
        table.Rows++;
        foreach (ColumnPlan column in table.Columns)
        {
            if (!column.IsJson && Node(item, column) is { } value)
                column.Kinds.Add(value);
        }
        foreach (TablePlan child in table.Children)
        {
            foreach ((JsonElement childItem, _) in Items(item, child.Path))
                Visit(child, childItem);
        }
    }

    private static async IAsyncEnumerable<IReadOnlyList<object?>> RowsAsync(
        RecordSource source, TablePlan table, [EnumeratorCancellation] CancellationToken ct)
    {
        List<TablePlan> chain = [];
        for (TablePlan? t = table; t is not null; t = t.Parent)
            chain.Insert(0, t);
        long[] counters = new long[chain.Count];
        await foreach (JsonElement record in source.Records(ct))
        {
            if (IsMissing(record))
                continue;
            counters[0]++;
            foreach (object?[] row in Descend(chain, counters, 0, record, counters[0], 0, 0))
                yield return row;
        }
    }

    /// <summary>The rows of the chain's last table under one item of the chain's table at <paramref name="level"/>.</summary>
    private static IEnumerable<object?[]> Descend(
        List<TablePlan> chain, long[] counters, int level, JsonElement item, long id, long parentId, long index)
    {
        if (level == chain.Count - 1)
        {
            yield return chain[level].Row(item, id, parentId, index);
            yield break;
        }
        foreach ((JsonElement child, long i) in Items(item, chain[level + 1].Path))
        {
            long childId = ++counters[level + 1];
            foreach (object?[] row in Descend(chain, counters, level + 1, child, childId, id, i))
                yield return row;
        }
    }

    /// <summary>
    /// The items of the list at <paramref name="path"/> inside <paramref name="item"/>, with each one's place in it: the
    /// values of every occurrence of the last key, a list's values one by one and a lone value as a list of one.
    /// Empty values aren't items, but still take their place.
    /// </summary>
    private static IEnumerable<(JsonElement Item, long Index)> Items(JsonElement item, string[] path)
    {
        JsonElement node = item;
        for (int i = 0; i < path.Length - 1; i++)
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(path[i], out node))
                yield break;
        }
        if (node.ValueKind != JsonValueKind.Object)
            yield break;
        long index = 0;
        foreach (JsonProperty property in node.EnumerateObject())
        {
            if (!string.Equals(property.Name, path[^1], StringComparison.Ordinal))
                continue;
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement value in property.Value.EnumerateArray())
                {
                    if (!IsMissing(value))
                        yield return (value, index);
                    index++;
                }
            }
            else if (!IsMissing(property.Value))
                yield return (property.Value, index++);
        }
    }

    /// <summary>The value a column reads from an item, or null when the item has none there.</summary>
    private static JsonElement? Node(JsonElement item, ColumnPlan column)
    {
        JsonElement node = item;
        foreach (string key in column.Keys)
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(key, out node))
                return null;
        }
        if (column.Mode == ColumnMode.Wrapped)
        {
            if (node.ValueKind == JsonValueKind.Object)
                return node.TryGetProperty(column.TextKey!, out JsonElement text) && text.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array) ? text : null;
            return node.ValueKind == JsonValueKind.Array ? null : node;
        }
        return IsMissing(node) ? null : node;
    }

    private static object? Value(this ColumnPlan column, JsonElement item)
    {
        switch (column.Mode)
        {
            case ColumnMode.JsonList:
                return ListJson(item, column.Keys);
            case ColumnMode.Json:
                return Node(item, column) is { } json ? CleanJson(json.GetRawText()) : null;
            default:
                return Node(item, column) is { } value ? column.Kinds.Convert(value) : null;
        }
    }

    /// <summary>Every value of the list at <paramref name="path"/>, as one JSON array.</summary>
    private static string? ListJson(JsonElement item, string[] path)
    {
        JsonElement node = item;
        for (int i = 0; i < path.Length - 1; i++)
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(path[i], out node))
                return null;
        }
        if (node.ValueKind != JsonValueKind.Object)
            return null;
        List<JsonElement> found = [];
        foreach (JsonProperty property in node.EnumerateObject())
        {
            if (string.Equals(property.Name, path[^1], StringComparison.Ordinal) && !IsMissing(property.Value))
                found.Add(property.Value);
        }
        if (found.Count == 0)
            return null;
        if (found is [{ ValueKind: JsonValueKind.Array } list])
            return CleanJson(list.GetRawText());
        var text = new StringBuilder("[");
        foreach (JsonElement value in found)
        {
            if (text.Length > 1)
                text.Append(',');
            if (value.ValueKind == JsonValueKind.Array)
                text.Append(value.GetRawText().AsSpan(1, value.GetRawText().Length - 2));
            else
                text.Append(value.GetRawText());
        }
        return CleanJson(text.Append(']').ToString());
    }

    private static readonly Regex NulEscape = new(@"(?<!\\)((?:\\\\)*)\\u0000", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>JSON PostgreSQL's jsonb accepts: the escaped NUL character it rejects is dropped.</summary>
    private static string CleanJson(string json) => json.Contains("\\u0000", StringComparison.Ordinal) ? NulEscape.Replace(json, "$1") : json;
}

/// <summary>
/// The kinds of plain JSON value one column holds, and from them its type (CON-308): numbers keep their JSON type, a
/// column of strings is typed by the CSV loader's rules, and any mix is text.
/// </summary>
internal sealed class ValueKinds
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
