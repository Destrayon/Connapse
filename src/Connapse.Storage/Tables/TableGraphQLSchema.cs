using System.Text;
using Connapse.Core;

namespace Connapse.Storage.Tables;

/// <summary>One table as a GraphQL type: its root field name, its visible columns by field name, and its child tables.</summary>
internal sealed class GraphQLTable(TableInfo info, string name)
{
    public TableInfo Info { get; } = info;

    /// <summary>The root field that reads the table; <c>&lt;Name&gt;_aggregate</c> aggregates it.</summary>
    public string Name { get; } = name;

    /// <summary>Visible columns by field name, in column order.</summary>
    public List<(string Field, TableColumn Column)> Columns { get; } = [];

    /// <summary>
    /// Child tables split from lists in this table's rows (CON-335), by field name: a child row belongs to the row whose
    /// <c>_row</c> equals its <c>_parent_row</c>.
    /// </summary>
    public List<(string Field, GraphQLTable Child)> Children { get; } = [];

    public TableColumn? Column(string field) => Find(Columns, field) is { } i ? Columns[i].Column : null;

    public GraphQLTable? Child(string field) => Find(Children, field) is { } i ? Children[i].Child : null;

    public bool HasColumn(string header) => Columns.Any(c => c.Column.Name == header);

    /// <summary>The field names a selection on this type may use, for error messages.</summary>
    public string FieldList() =>
        string.Join(", ", Columns.Select(c => c.Field).Concat(Children.Select(c => c.Field)));

    /// <summary>An exact match, else the only match ignoring case.</summary>
    private static int? Find<T>(List<(string Field, T Value)> list, string field)
    {
        int exact = list.FindIndex(c => c.Field == field);
        if (exact >= 0)
            return exact;
        int[] loose = list.Select((c, i) => (c.Field, i))
            .Where(c => string.Equals(c.Field, field, StringComparison.OrdinalIgnoreCase)).Select(c => c.i).ToArray();
        return loose.Length == 1 ? loose[0] : null;
    }
}

/// <summary>
/// The GraphQL schema one caller sees over one container's tables (CON-336): a root field per readable table, named
/// from the table's name, and a field per visible column, named from its header. Names become valid GraphQL names
/// (anything but letters, digits and <c>_</c> becomes <c>_</c>, a leading digit gets a <c>_</c>, and a clash gets
/// <c>_2</c>, <c>_3</c>, ...). Built only from what the caller may read, so it can't name anything else.
/// </summary>
internal sealed class TableGraphQLSchema
{
    /// <summary>How many characters of the schema text may list columns; later tables are named only.</summary>
    internal const int MaxSchemaText = 16000;

    internal const string SchemaField = "_schema";
    internal const string AggregateSuffix = "_aggregate";

    private readonly List<GraphQLTable> _tables;

    private TableGraphQLSchema(List<GraphQLTable> tables) => _tables = tables;

    public IReadOnlyList<GraphQLTable> Tables => _tables;

    /// <summary>The table read by root field <paramref name="name"/>, exactly or as the only match ignoring case.</summary>
    public GraphQLTable? Table(string name)
    {
        GraphQLTable? exact = _tables.Find(t => t.Name == name);
        if (exact is not null)
            return exact;
        List<GraphQLTable> loose = _tables.FindAll(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        return loose.Count == 1 ? loose[0] : null;
    }

    public static TableGraphQLSchema Build(IEnumerable<TableInfo> readable)
    {
        HashSet<string> used = new(StringComparer.Ordinal) { SchemaField };
        List<GraphQLTable> tables = [];
        foreach (TableInfo info in readable.OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Id))
        {
            string name = Unique(FieldName(info.Name), n => !used.Contains(n) && !used.Contains(n + AggregateSuffix)
                && !(n.EndsWith(AggregateSuffix, StringComparison.Ordinal) && used.Contains(n[..^AggregateSuffix.Length])));
            used.Add(name);
            used.Add(name + AggregateSuffix);

            GraphQLTable table = new(info, name);
            HashSet<string> fields = new(StringComparer.Ordinal);
            foreach (int i in info.VisibleColumnIndexes)
            {
                string field = Unique(FieldName(info.Columns[i].Name), f => !fields.Contains(f));
                fields.Add(field);
                table.Columns.Add((field, info.Columns[i]));
            }
            tables.Add(table);
        }

        // A child table is named "<parent>.<path>" and joins its _parent_row to the parent's _row; the longest name
        // that prefixes it is its parent.
        foreach (GraphQLTable child in tables.Where(t => t.HasColumn(TableGraphQLNames.ParentColumn)))
        {
            GraphQLTable? parent = tables
                .Where(p => p != child && p.HasColumn(TableGraphQLNames.RowColumn)
                    && child.Info.Name.Length > p.Info.Name.Length + 1
                    && child.Info.Name.StartsWith(p.Info.Name + ".", StringComparison.Ordinal))
                .MaxBy(p => p.Info.Name.Length);
            if (parent is null)
                continue;
            string path = child.Info.Name[(parent.Info.Name.Length + 1)..];
            string field = Unique(FieldName(path), f => parent.Columns.All(c => c.Field != f) && parent.Children.All(c => c.Field != f));
            parent.Children.Add((field, child));
        }

        return new TableGraphQLSchema(tables);
    }

    /// <summary>A valid GraphQL name for <paramref name="text"/>.</summary>
    internal static string FieldName(string text)
    {
        StringBuilder name = new(text.Length + 1);
        foreach (char c in text)
            name.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
        if (name.Length == 0 || char.IsAsciiDigit(name[0]))
            name.Insert(0, '_');
        if (name.Length >= 2 && name[0] == '_' && name[1] == '_')
            name.Insert(0, 't'); // names starting "__" are reserved for introspection
        return name.ToString();
    }

    private static string Unique(string name, Func<string, bool> free)
    {
        if (free(name))
            return name;
        for (int i = 2; ; i++)
        {
            if (free($"{name}_{i}"))
                return $"{name}_{i}";
        }
    }

    /// <summary>
    /// The schema as SDL, one type per table with each column's type and, where the field name differs, its header.
    /// Past <see cref="MaxSchemaText"/> characters, later tables are named without their fields; <c>_schema(table:)</c>
    /// gives one table's type in full.
    /// </summary>
    public string Sdl(GraphQLTable? only = null)
    {
        IEnumerable<GraphQLTable> tables = only is null ? _tables : [only];
        if (!tables.Any())
            return "# This container has no tables you can query.";

        StringBuilder text = new();
        if (only is null)
        {
            text.Append("# Each table is a root field: <table>(where, order_by, limit, offset, join, seen_as_of) returns rows, and\n")
                .Append("# <table>_aggregate(where, having, order_by, limit, join, seen_as_of) { <group column> count sum { <column> } }\n")
                .Append("# returns one row per group of the plain columns selected.\n");
        }
        List<string> unlisted = [];
        foreach (GraphQLTable table in tables)
        {
            string type = Type(table);
            if (only is null && text.Length + type.Length > MaxSchemaText)
            {
                unlisted.Add(table.Name);
                continue;
            }
            text.Append(type);
        }
        if (unlisted.Count > 0)
            text.Append("# More tables; query _schema(table: \"<name>\") for each one's fields: ")
                .AppendJoin(", ", unlisted).Append('\n');
        return text.ToString().TrimEnd('\n');
    }

    private static string Type(GraphQLTable table)
    {
        StringBuilder text = new();
        text.Append("type ").Append(table.Name).Append(" { # \"").Append(table.Info.Name).Append("\", ")
            .Append(table.Info.RowCount).Append(" rows");
        if (table.Info.HasHistory)
            text.Append("; keeps history, seen_as_of reads it as of a time");
        text.Append('\n');
        foreach ((string field, TableColumn column) in table.Columns)
        {
            text.Append("  ").Append(field).Append(": ").Append(TypeName(column.Type));
            if (field != column.Name)
                text.Append(" # \"").Append(column.Name).Append('"');
            text.Append('\n');
        }
        foreach ((string field, GraphQLTable child) in table.Children)
            text.Append("  ").Append(field).Append(": [").Append(child.Name).Append("] # child rows: ")
                .Append(child.Name).Append('.').Append(TableGraphQLNames.ParentColumn).Append(" = ")
                .Append(TableGraphQLNames.RowColumn).Append('\n');
        text.Append("}\n");
        return text.ToString();
    }

    internal static string TypeName(TableColumnType type) => type switch
    {
        TableColumnType.Text => "String",
        TableColumnType.WholeNumber => "Int",
        TableColumnType.Number => "Float",
        TableColumnType.Boolean => "Boolean",
        TableColumnType.Date => "Date",
        TableColumnType.DateTime => "DateTime",
        TableColumnType.Json => "JSON",
        _ => "String",
    };
}

/// <summary>The columns child tables join on (CON-335); kept here since Storage can't see the Ingestion flattener.</summary>
internal static class TableGraphQLNames
{
    public const string RowColumn = "_row";
    public const string ParentColumn = "_parent_row";
}
