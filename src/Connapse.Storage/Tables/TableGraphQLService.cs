using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Core;
using Connapse.Core.Interfaces;
using GraphQLParser;
using GraphQLParser.AST;
using GraphQLParser.Exceptions;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.Tables;

/// <summary>
/// Answers GraphQL over a container's tables (CON-336, docs/research/con-336-graphql-for-agents-2026-11-12.md). The
/// query is parsed with GraphQL-Parser and checked against a schema built for this caller from the tables in its
/// <see cref="TableAccess"/> and their visible columns (<see cref="TableGraphQLSchema"/>), so anything else is an
/// unknown name, exactly as a name that doesn't exist. Each root field and each nested list becomes one
/// <see cref="TableReadRequest"/> or <see cref="TableAggregateRequest"/> run by <see cref="ITableQueryService"/>, which
/// checks access again, trims merged tables to readable rows and applies its limits. GraphQL adds no SQL of its own.
/// <para>
/// Only a single query operation runs: no mutations, subscriptions, fragments, variables, directives or introspection
/// (<c>_schema</c> gives the schema instead). A nested list of another table reads every parent's rows in one batched
/// request filtered by the parents' key values, so a query costs at most one request per field, and the depth, field
/// and request caps below bound the rest.
/// </para>
/// </summary>
public sealed class TableGraphQLService(
    ITableStore tables,
    ITableQueryService queries,
    ILogger<TableGraphQLService> logger) : ITableGraphQLService
{
    internal const int MaxQueryLength = 10_000;
    internal const int MaxRootFields = 10;
    internal const int MaxAliases = 15;
    internal const int MaxFields = 300;
    internal const int MaxTableDepth = 4;
    internal const int MaxRequests = 40;
    internal const int MaxAnswerBytes = TableQueryRunner.MaxAnswerBytes;
    internal const int DefaultLimit = 100;
    internal const int MaxLimit = 1024;
    private const int MaxParseDepth = 64;
    private const int MaxInValues = 1000;
    private const string JoinAlias = "joined";

    public async Task<TableGraphQLResult> RunAsync(Guid containerId, TableAccess access, string query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(query);
        if (!access.Checked)
            throw new TableQueryException(TableAccess.UncheckedMessage);
        if (query.Length > MaxQueryLength)
            throw new TableQueryException($"A query can be at most {MaxQueryLength} characters.");

        GraphQLSelectionSet root = Parse(query);
        TableGraphQLSchema schema = TableGraphQLSchema.Build(
            (await tables.ListAsync(containerId, ct)).Where(t => access.CanRead(t) && !MemoryTable.Is(t)));
        Run run = new(queries, containerId, access, schema, ct);

        JsonObject data = [];
        foreach (GraphQLField field in Fields(root))
        {
            string key = Key(field);
            if (data.ContainsKey(key))
                throw new TableQueryException($"Two root fields are both named \"{key}\"; give one an alias.");
            data[key] = await run.RootAsync(field);
        }

        string json = data.ToJsonString();
        int bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes > MaxAnswerBytes)
            throw new TableQueryException(
                $"The answer is {bytes} bytes, over the {MaxAnswerBytes}-byte cap. Ask for fewer columns, add a where, or lower limit.");
        logger.LogDebug("GraphQL in {ContainerId} made {Requests} table requests", containerId, run.Requests);
        string? truncated = run.Truncated.Count == 0 ? null : string.Join(" ", run.Truncated);
        return new TableGraphQLResult(JsonDocument.Parse(json).RootElement.Clone(), truncated is not null, truncated);
    }

    /// <summary>Parses the query and refuses everything but one query operation of plain fields within the caps.</summary>
    internal static GraphQLSelectionSet Parse(string query)
    {
        GraphQLDocument document;
        try
        {
            document = Parser.Parse(query, new ParserOptions { Ignore = IgnoreOptions.All, MaxDepth = MaxParseDepth });
        }
        catch (GraphQLParserException ex)
        {
            throw new TableQueryException($"The query isn't valid GraphQL: {ex.Description} (line {ex.Location.Line}, column {ex.Location.Column}).");
        }

        if (document.Definitions.Any(d => d is GraphQLFragmentDefinition))
            throw new TableQueryException("Fragments aren't supported; write the fields out in the query.");
        List<GraphQLOperationDefinition> operations = document.Definitions.OfType<GraphQLOperationDefinition>().ToList();
        if (operations.Count != 1 || operations.Count != document.Definitions.Count)
            throw new TableQueryException("Send exactly one query, such as { orders(limit: 5) { id total } }.");
        GraphQLOperationDefinition operation = operations[0];
        if (operation.Operation != OperationType.Query)
            throw new TableQueryException("Only queries run here; tables can only be read.");
        if (operation.Variables is { Count: > 0 })
            throw new TableQueryException("Variables aren't supported; write values into the query.");
        if (operation.Directives is { Count: > 0 })
            throw new TableQueryException("Directives aren't supported.");

        int fields = 0, aliases = 0;
        Check(operation.SelectionSet, ref fields, ref aliases);
        if (operation.SelectionSet.Selections.Count > MaxRootFields)
            throw new TableQueryException($"A query can have at most {MaxRootFields} root fields.");
        if (aliases > MaxAliases)
            throw new TableQueryException($"A query can have at most {MaxAliases} aliases.");
        if (fields > MaxFields)
            throw new TableQueryException($"A query can have at most {MaxFields} fields.");
        return operation.SelectionSet;
    }

    private static void Check(GraphQLSelectionSet set, ref int fields, ref int aliases)
    {
        foreach (ASTNode selection in set.Selections)
        {
            if (selection is not GraphQLField field)
                throw new TableQueryException("Fragments aren't supported; write the fields out in the query.");
            if (field.Directives is { Count: > 0 })
                throw new TableQueryException("Directives aren't supported.");
            if (field.Name.StringValue.StartsWith("__", StringComparison.Ordinal))
                throw new TableQueryException($"Introspection isn't supported; query {{ {TableGraphQLSchema.SchemaField} }} for the schema.");
            fields++;
            if (field.Alias is not null)
                aliases++;
            if (field.SelectionSet is not null)
                Check(field.SelectionSet, ref fields, ref aliases);
        }
    }

    private static IEnumerable<GraphQLField> Fields(GraphQLSelectionSet? set) =>
        set?.Selections.Cast<GraphQLField>() ?? [];

    private static string Key(GraphQLField field) => (field.Alias?.Name ?? field.Name).StringValue;

    /// <summary>One query's state: the caller's schema, the requests made so far and the lists that were cut.</summary>
    private sealed class Run(ITableQueryService queries, Guid containerId, TableAccess access, TableGraphQLSchema schema, CancellationToken ct)
    {
        public int Requests { get; private set; }

        public List<string> Truncated { get; } = [];

        public async Task<JsonNode?> RootAsync(GraphQLField field)
        {
            string name = field.Name.StringValue;
            if (name == TableGraphQLSchema.SchemaField)
            {
                if (field.SelectionSet is not null)
                    throw new TableQueryException($"{TableGraphQLSchema.SchemaField} returns text; it takes no fields.");
                Arguments args = new(field, "table");
                if (args.Get("table") is { } value)
                {
                    string table = Text(value, "table");
                    return schema.Sdl(schema.Table(table) ?? throw UnknownTable(table));
                }
                return schema.Sdl();
            }

            if (schema.Table(name) is { } read)
                return new JsonArray([.. (await ReadAsync(read, field, Key(field), depth: 1, restrict: null)).Rows]);
            if (name.EndsWith(TableGraphQLSchema.AggregateSuffix, StringComparison.Ordinal)
                && schema.Table(name[..^TableGraphQLSchema.AggregateSuffix.Length]) is { } aggregated)
                return await AggregateAsync(aggregated, field, Key(field));
            throw UnknownTable(name);
        }

        private TableQueryException UnknownTable(string name) => new(
            $"There is no table \"{name}\" you can query. These are the tables you can query:\n{schema.Sdl()}");

        /// <summary>
        /// Reads rows of <paramref name="table"/> for <paramref name="field"/>: its columns, a joined table's columns as
        /// a nested object, and nested lists of child or other tables, each read in one batched request.
        /// <paramref name="restrict"/> limits the read to the rows a parent list asked for.
        /// </summary>
        private async Task<(List<JsonObject> Rows, List<object?> Keys)> ReadAsync(
            GraphQLTable table, GraphQLField field, string path, int depth, (string Header, IReadOnlyList<object?> Values)? restrict)
        {
            if (depth > MaxTableDepth)
                throw new TableQueryException($"Lists can nest at most {MaxTableDepth} tables deep.");
            if (field.SelectionSet is null)
                throw new TableQueryException($"Pick the fields of {table.Name} to return, such as {{ {table.Columns.FirstOrDefault().Field ?? "..."} }}.");

            bool nested = restrict is not null;
            Arguments args = nested
                ? new(field, "where", "order_by", "limit", "join", "on")
                : new(field, "where", "order_by", "limit", "offset", "join", "seen_as_of");
            Scope scope = Scope.For(schema, table, args.Get("join"));

            List<string> columns = [];
            int Column(string header)
            {
                int at = columns.IndexOf(header);
                if (at >= 0)
                    return at;
                columns.Add(header);
                return columns.Count - 1;
            }

            List<(string Key, int Column, TableColumnType Type)> plain = [];
            List<(string Key, List<(string Key, int Column, TableColumnType Type)> Fields)> joinedObjects = [];
            List<(string Key, GraphQLField Field, GraphQLTable Target, int Local, string Remote)> lists = [];
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (GraphQLField selected in Fields(field.SelectionSet))
            {
                string key = Key(selected);
                if (!keys.Add(key))
                    throw new TableQueryException($"{table.Name} selects \"{key}\" twice; give one an alias.");
                string name = selected.Name.StringValue;

                if (selected.SelectionSet is null && table.Column(name) is { } column)
                {
                    NoArguments(selected);
                    plain.Add((key, Column(column.Name), column.Type));
                }
                else if (table.Child(name) is { } child && selected.Arguments?.ValueFor("on") is null)
                    lists.Add((key, selected, child, Column(TableGraphQLNames.RowColumn), TableGraphQLNames.ParentColumn));
                else if (schema.Table(name) is { } other && selected.Arguments?.ValueFor("on") is { } on)
                {
                    (string local, string remote) = OnPair(on, table, other);
                    lists.Add((key, selected, other, Column(local), remote));
                }
                else if (scope.Joined is { } joined && joined.Name == name && selected.SelectionSet is not null)
                {
                    NoArguments(selected);
                    List<(string, int, TableColumnType)> fields = [];
                    foreach (GraphQLField inner in Fields(selected.SelectionSet))
                    {
                        NoArguments(inner);
                        TableColumn c = joined.Column(inner.Name.StringValue) ?? throw UnknownField(joined, inner.Name.StringValue);
                        fields.Add((Key(inner), Column($"{JoinAlias}.{c.Name}"), c.Type));
                    }
                    joinedObjects.Add((key, fields));
                }
                else
                    throw UnknownField(table, name, scope.Joined, selected.SelectionSet is null);
            }

            int keyColumn = restrict is { } r ? Column(r.Header) : -1;
            TableFilter? where = scope.Where(args.Get("where"));
            if (restrict is { } limitTo)
            {
                TableFilter within = new TableCondition(new TableField(limitTo.Header), TableFilterOperator.In, limitTo.Values);
                where = where is null ? within : new TableAndFilter([where, within]);
            }

            int limit = Limit(args.Get("limit"));
            TableReadRequest request = new(
                table.Info.Id,
                columns,
                where,
                scope.Order(args.Get("order_by"), aggregate: false),
                nested ? MaxLimit : limit,
                nested ? null : Int(args.Get("offset"), "offset"),
                SeenAsOf(args.Get("seen_as_of")),
                scope.Join);
            TableQueryResult result = await ReadAsync(request, path, nested);

            List<JsonObject> rows = [];
            List<object?> rowKeys = [];
            foreach (IReadOnlyList<object?> cells in result.Rows)
            {
                JsonObject row = [];
                foreach ((string key, int column, TableColumnType type) in plain)
                    row[key] = Cell(cells[column], type);
                foreach ((string key, List<(string Key, int Column, TableColumnType Type)> fields) in joinedObjects)
                {
                    JsonObject inner = [];
                    foreach ((string k, int column, TableColumnType type) in fields)
                        inner[k] = Cell(cells[column], type);
                    row[key] = inner;
                }
                rows.Add(row);
                rowKeys.Add(keyColumn >= 0 ? cells[keyColumn] : null);
            }

            foreach ((string key, GraphQLField listField, GraphQLTable target, int local, string remote) in lists)
            {
                List<object?> parentValues = result.Rows.Select(c => c[local]).ToList();
                Dictionary<string, List<JsonObject>> byParent = new(StringComparer.Ordinal);
                List<object?> distinct = parentValues.Where(v => v is not null).DistinctBy(Normal).ToList();
                foreach (object?[] chunk in distinct.Chunk(MaxInValues))
                {
                    (List<JsonObject> childRows, List<object?> childKeys) =
                        await ReadAsync(target, listField, $"{path}.{key}", depth + 1, (remote, chunk));
                    for (int i = 0; i < childRows.Count; i++)
                    {
                        string parent = Normal(childKeys[i]);
                        if (!byParent.TryGetValue(parent, out List<JsonObject>? list))
                            byParent[parent] = list = [];
                        list.Add(childRows[i]);
                    }
                }

                int perParent = Limit(new Arguments(listField, "where", "order_by", "limit", "join", "on").Get("limit"));
                bool cut = false;
                for (int i = 0; i < rows.Count; i++)
                {
                    List<JsonObject> children = parentValues[i] is { } v && byParent.TryGetValue(Normal(v), out List<JsonObject>? found) ? found : [];
                    cut |= children.Count > perParent;
                    // A child row reached through two parents with the same key is copied, since a node has one parent.
                    rows[i][key] = new JsonArray([.. children.Take(perParent).Select(c => c.Parent is null ? c : (JsonObject)c.DeepClone())]);
                }
                if (cut)
                    Truncated.Add($"{path}.{key}: some lists had more than {perParent} rows; only the first {perParent} are shown. Raise limit to see more.");
            }
            return (rows, rowKeys);
        }

        private async Task<TableQueryResult> ReadAsync(TableReadRequest request, string path, bool nested)
        {
            Count();
            TableQueryResult result = await queries.ReadAsync(containerId, access, request, ct);
            if (result.Truncated)
                Truncated.Add(nested
                    ? $"{path}: more than {MaxLimit} rows matched across all the parent rows, so some lists are incomplete. Ask for fewer parent rows or add a where."
                    : $"{path}: {result.TruncatedReason}");
            return result;
        }

        private void Count()
        {
            if (++Requests > MaxRequests)
                throw new TableQueryException($"The query needs more than {MaxRequests} table reads. Split it into smaller queries.");
        }

        /// <summary>
        /// Aggregates <paramref name="table"/>: the plain columns selected are the groups, and <c>count</c>,
        /// <c>count_distinct</c>, <c>sum</c>, <c>avg</c>, <c>min</c> and <c>max</c> the aggregates, one row per group.
        /// </summary>
        private async Task<JsonArray> AggregateAsync(GraphQLTable table, GraphQLField field, string path)
        {
            if (field.SelectionSet is null)
                throw new TableQueryException($"Pick what to compute, such as {{ count }} or {{ {table.Columns.FirstOrDefault().Field ?? "..."} count }}.");
            Arguments args = new(field, "where", "having", "order_by", "limit", "join", "seen_as_of");
            Scope scope = Scope.For(schema, table, args.Get("join"));

            List<string> groupBy = [];
            List<TableField> aggregates = [];
            // Each output: the key, whether it's an aggregate, its index, its type, and for an aggregate of columns the
            // inner keys.
            List<(string Key, bool Group, int Index, TableColumnType? Type)> outputs = [];
            List<(string Key, List<(string Key, int Index, TableColumnType? Type)> Fields)> objects = [];
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (GraphQLField selected in Fields(field.SelectionSet))
            {
                string key = Key(selected);
                if (!keys.Add(key))
                    throw new TableQueryException($"{table.Name}{TableGraphQLSchema.AggregateSuffix} selects \"{key}\" twice; give one an alias.");
                NoArguments(selected);
                string name = selected.Name.StringValue;
                if (name == "count" && selected.SelectionSet is null)
                {
                    aggregates.Add(new TableField(null, TableAggregateFunction.Count));
                    outputs.Add((key, false, aggregates.Count - 1, TableColumnType.WholeNumber));
                }
                else if (Function(name) is { } function && selected.SelectionSet is not null)
                {
                    List<(string, int, TableColumnType?)> fields = [];
                    foreach ((string innerKey, string header, TableColumnType type) in scope.Columns(selected))
                    {
                        aggregates.Add(new TableField(header, function));
                        fields.Add((innerKey, aggregates.Count - 1, function is TableAggregateFunction.CountDistinct ? TableColumnType.WholeNumber
                            : function is TableAggregateFunction.Sum or TableAggregateFunction.Avg ? null : type));
                    }
                    objects.Add((key, fields));
                }
                else if (selected.SelectionSet is null && table.Column(name) is { } column)
                {
                    if (!groupBy.Contains(column.Name))
                        groupBy.Add(column.Name);
                    outputs.Add((key, true, groupBy.IndexOf(column.Name), column.Type));
                }
                else if (scope.Joined is { } joined && joined.Name == name && selected.SelectionSet is not null)
                {
                    List<(string, int, TableColumnType?)> fields = [];
                    foreach ((string innerKey, string header, TableColumnType type) in scope.Columns(selected, joinedOnly: true))
                    {
                        if (!groupBy.Contains(header))
                            groupBy.Add(header);
                        fields.Add((innerKey, -1 - groupBy.IndexOf(header), type));
                    }
                    objects.Add((key, fields));
                }
                else
                    throw new TableQueryException(
                        $"{table.Name}{TableGraphQLSchema.AggregateSuffix} has no field \"{name}\". Select columns of {table.Name} to group by "
                        + $"({table.FieldList()}), count, or count_distinct, sum, avg, min or max with the columns inside, such as sum {{ {table.Columns.FirstOrDefault().Field ?? "..."} }}.");
            }
            if (aggregates.Count == 0)
                throw new TableQueryException($"Select at least one aggregate, such as count, in {table.Name}{TableGraphQLSchema.AggregateSuffix}.");

            TableAggregateRequest request = new(
                table.Info.Id,
                aggregates,
                groupBy,
                scope.Where(args.Get("where")),
                scope.Having(args.Get("having")),
                scope.Order(args.Get("order_by"), aggregate: true),
                Limit(args.Get("limit")),
                SeenAsOf(args.Get("seen_as_of")),
                scope.Join);
            Count();
            TableQueryResult result = await queries.AggregateAsync(containerId, access, request, ct);
            if (result.Truncated)
                Truncated.Add($"{path}: {result.TruncatedReason}");

            JsonArray rows = [];
            foreach (IReadOnlyList<object?> cells in result.Rows)
            {
                // The answer's columns are the groups, then the aggregates, in request order.
                JsonObject row = [];
                foreach ((string key, bool group, int index, TableColumnType? type) in outputs)
                    row[key] = Cell(cells[group ? index : groupBy.Count + index], type);
                foreach ((string key, List<(string Key, int Index, TableColumnType? Type)> fields) in objects)
                {
                    JsonObject inner = [];
                    foreach ((string k, int index, TableColumnType? type) in fields)
                        inner[k] = Cell(cells[index < 0 ? -1 - index : groupBy.Count + index], type);
                    row[key] = inner;
                }
                rows.Add(row);
            }
            return rows;
        }

        private TableQueryException UnknownField(GraphQLTable table, string name, GraphQLTable? joined = null, bool leaf = true)
        {
            string hint = !leaf && schema.Table(name) is { } other
                ? $" To list rows of {other.Name} here, say which columns match: {name}(on: {{{table.Columns.FirstOrDefault().Field ?? "column"}: <{other.Name} column>}}) {{ ... }}."
                : "";
            string fields = table.FieldList() + (joined is null ? "" : $", {joined.Name} {{ ... }} (the joined table)");
            return new TableQueryException($"{table.Name} has no field \"{name}\". Its fields are: {fields}.{hint}");
        }

        private (string Local, string Remote) OnPair(GraphQLValue on, GraphQLTable table, GraphQLTable other)
        {
            if (on is not GraphQLObjectValue { Fields: { Count: 1 } fields })
                throw new TableQueryException($"on takes one pair of columns, such as on: {{<{table.Name} column>: <{other.Name} column>}}.");
            GraphQLObjectField pair = fields[0];
            TableColumn local = table.Column(pair.Name.StringValue) ?? throw UnknownField(table, pair.Name.StringValue);
            string remoteName = Text(pair.Value, "on");
            TableColumn remote = other.Column(remoteName) ?? throw UnknownField(other, remoteName);
            return (local.Name, remote.Name);
        }
    }

    /// <summary>
    /// What a field's column names mean: the table's own columns, and when the field joins another table, that
    /// table's columns inside an object named after it.
    /// </summary>
    private sealed class Scope
    {
        private Scope(GraphQLTable table, GraphQLTable? joined, TableJoin? join)
        {
            Table = table;
            Joined = joined;
            Join = join;
        }

        public GraphQLTable Table { get; }

        public GraphQLTable? Joined { get; }

        public TableJoin? Join { get; }

        public static Scope For(TableGraphQLSchema schema, GraphQLTable table, GraphQLValue? join)
        {
            if (join is null)
                return new Scope(table, null, null);
            if (join is not GraphQLObjectValue value)
                throw new TableQueryException(JoinUsage);
            string? name = null;
            GraphQLValue? on = null;
            TableJoinKind kind = TableJoinKind.Inner;
            foreach (GraphQLObjectField f in value.Fields ?? [])
            {
                switch (f.Name.StringValue)
                {
                    case "table": name = Text(f.Value, "join.table"); break;
                    case "on": on = f.Value; break;
                    case "kind":
                        string k = Text(f.Value, "join.kind");
                        kind = k.Equals("left", StringComparison.OrdinalIgnoreCase) ? TableJoinKind.Left
                            : k.Equals("inner", StringComparison.OrdinalIgnoreCase) ? TableJoinKind.Inner
                            : throw new TableQueryException("join.kind is inner or left.");
                        break;
                    default: throw new TableQueryException(JoinUsage);
                }
            }
            if (name is null || on is not GraphQLObjectValue { Fields: { Count: 1 } pairs })
                throw new TableQueryException(JoinUsage);
            GraphQLTable joined = schema.Table(name) ?? throw new TableQueryException(
                $"There is no table \"{name}\" you can join. Query {{ {TableGraphQLSchema.SchemaField} }} for the tables you can query.");
            TableColumn local = table.Column(pairs[0].Name.StringValue) ?? throw new TableQueryException(
                $"{table.Name} has no column \"{pairs[0].Name.StringValue}\". Its columns are: {table.FieldList()}.");
            string remoteName = Text(pairs[0].Value, "join.on");
            TableColumn remote = joined.Column(remoteName) ?? throw new TableQueryException(
                $"{joined.Name} has no column \"{remoteName}\". Its columns are: {joined.FieldList()}.");
            return new Scope(table, joined, new TableJoin(joined.Info.Id, JoinAlias, local.Name, remote.Name, kind));
        }

        private const string JoinUsage =
            "join takes {table: <table>, on: {<column>: <joined table column>}, kind: inner or left}; the joined table's columns are then fields of an object named after it.";

        /// <summary>The columns selected inside <paramref name="field"/>, the joined table's under its name.</summary>
        public IEnumerable<(string Key, string Header, TableColumnType Type)> Columns(GraphQLField field, bool joinedOnly = false)
        {
            foreach (GraphQLField inner in Fields(field.SelectionSet))
            {
                NoArguments(inner);
                string name = inner.Name.StringValue;
                if (joinedOnly)
                {
                    TableColumn c = Joined!.Column(name) ?? throw Unknown(Joined, name);
                    yield return (Key(inner), $"{JoinAlias}.{c.Name}", c.Type);
                }
                else if (inner.SelectionSet is null && Table.Column(name) is { } column)
                    yield return (Key(inner), column.Name, column.Type);
                else if (Joined is not null && Joined.Name == name && inner.SelectionSet is not null)
                {
                    foreach (GraphQLField j in Fields(inner.SelectionSet))
                    {
                        TableColumn c = Joined.Column(j.Name.StringValue) ?? throw Unknown(Joined, j.Name.StringValue);
                        yield return (Key(j), $"{JoinAlias}.{c.Name}", c.Type);
                    }
                }
                else
                    throw Unknown(Table, name);
            }
        }

        private TableQueryException Unknown(GraphQLTable table, string name) => new(
            $"{table.Name} has no column \"{name}\". Its columns are: {string.Join(", ", table.Columns.Select(c => c.Field))}"
            + (Joined is not null && table == Table ? $", and {Joined.Name} {{ ... }} for the joined table's." : "."));

        /// <summary>The header a filter or order names, or null when <paramref name="name"/> is the joined table.</summary>
        private string? Header(string name, bool inJoined)
        {
            if (inJoined)
                return $"{JoinAlias}.{(Joined!.Column(name) ?? throw Unknown(Joined, name)).Name}";
            if (Table.Column(name) is { } column)
                return column.Name;
            if (Joined is not null && Joined.Name == name)
                return null;
            throw Unknown(Table, name);
        }

        public TableFilter? Where(GraphQLValue? value) => value is null ? null : Filter(value, inJoined: false, "where");

        private TableFilter? Filter(GraphQLValue value, bool inJoined, string where)
        {
            if (value is not GraphQLObjectValue obj)
                throw new TableQueryException($"{where} takes an object, such as {{amount: {{_gt: 100}}}}.");
            List<TableFilter> all = [];
            foreach (GraphQLObjectField f in obj.Fields ?? [])
            {
                string name = f.Name.StringValue;
                switch (name)
                {
                    case "_and":
                        all.Add(new TableAndFilter(List(f.Value).Select(v => Filter(v, inJoined, where)).OfType<TableFilter>().ToList()));
                        break;
                    case "_or":
                        all.Add(new TableOrFilter(List(f.Value).Select(v => Filter(v, inJoined, where)).OfType<TableFilter>().ToList()));
                        break;
                    case "_not":
                        if (Filter(f.Value, inJoined, where) is { } inner)
                            all.Add(new TableNotFilter(inner));
                        break;
                    default:
                        string? header = Header(name, inJoined);
                        if (header is null)
                        {
                            if (Filter(f.Value, inJoined: true, where) is { } joined)
                                all.Add(joined);
                        }
                        else
                            all.AddRange(Conditions(new TableField(header), f.Value, name));
                        break;
                }
            }
            return all.Count switch { 0 => null, 1 => all[0], _ => new TableAndFilter(all) };
        }

        /// <summary>The comparisons in <c>{_gt: 1, _lt: 5}</c> on <paramref name="field"/>.</summary>
        private static IEnumerable<TableFilter> Conditions(TableField field, GraphQLValue value, string name)
        {
            if (value is not GraphQLObjectValue { Fields: { Count: > 0 } ops })
                throw new TableQueryException($"Compare {name} with an operator, such as {name}: {{_eq: \"x\"}}. Operators: {Operators}.");
            foreach (GraphQLObjectField op in ops)
            {
                object? literal = Literal(op.Value);
                yield return op.Name.StringValue switch
                {
                    "_eq" when literal is null => new TableCondition(field, TableFilterOperator.IsNull, []),
                    "_eq" => new TableCondition(field, TableFilterOperator.Equal, [literal]),
                    "_neq" when literal is null => new TableNotFilter(new TableCondition(field, TableFilterOperator.IsNull, [])),
                    "_neq" => new TableCondition(field, TableFilterOperator.NotEqual, [literal]),
                    "_lt" => new TableCondition(field, TableFilterOperator.Less, [literal]),
                    "_lte" => new TableCondition(field, TableFilterOperator.LessOrEqual, [literal]),
                    "_gt" => new TableCondition(field, TableFilterOperator.Greater, [literal]),
                    "_gte" => new TableCondition(field, TableFilterOperator.GreaterOrEqual, [literal]),
                    "_in" => new TableCondition(field, TableFilterOperator.In, Values(literal)),
                    "_nin" => new TableNotFilter(new TableCondition(field, TableFilterOperator.In, Values(literal))),
                    "_between" => new TableCondition(field, TableFilterOperator.Between, Values(literal)),
                    "_is_null" when literal is bool isNull => isNull
                        ? new TableCondition(field, TableFilterOperator.IsNull, [])
                        : new TableNotFilter(new TableCondition(field, TableFilterOperator.IsNull, [])),
                    "_is_null" => throw new TableQueryException("_is_null takes true or false."),
                    "_contains" => new TableCondition(field, TableFilterOperator.Contains, [literal]),
                    "_starts_with" => new TableCondition(field, TableFilterOperator.StartsWith, [literal]),
                    string other => throw new TableQueryException($"\"{other}\" isn't an operator. Operators: {Operators}."),
                };
            }
        }

        private const string Operators = "_eq, _neq, _lt, _lte, _gt, _gte, _in, _nin, _between, _is_null, _contains, _starts_with";

        private static IReadOnlyList<object?> Values(object? literal) =>
            literal as IReadOnlyList<object?> ?? [literal];

        /// <summary>
        /// A filter on aggregates: <c>{count: {_gt: 5}, sum: {amount: {_gt: 100}}}</c>, with <c>_and</c>, <c>_or</c> and
        /// <c>_not</c>.
        /// </summary>
        public TableFilter? Having(GraphQLValue? value)
        {
            if (value is null)
                return null;
            if (value is not GraphQLObjectValue obj)
                throw new TableQueryException("having takes an object, such as {count: {_gt: 5}} or {sum: {amount: {_gt: 100}}}.");
            List<TableFilter> all = [];
            foreach (GraphQLObjectField f in obj.Fields ?? [])
            {
                string name = f.Name.StringValue;
                if (name is "_and" or "_or")
                {
                    List<TableFilter> parts = List(f.Value).Select(Having).OfType<TableFilter>().ToList();
                    all.Add(name == "_and" ? new TableAndFilter(parts) : new TableOrFilter(parts));
                }
                else if (name == "_not")
                {
                    if (Having(f.Value) is { } inner)
                        all.Add(new TableNotFilter(inner));
                }
                else if (name == "count")
                    all.AddRange(Conditions(new TableField(null, TableAggregateFunction.Count), f.Value, "count"));
                else if (Function(name) is { } function)
                {
                    foreach ((TableField field, GraphQLValue ops, string label) in AggregateFields(f.Value, function, name))
                        all.AddRange(Conditions(field, ops, label));
                }
                else
                    throw new TableQueryException(
                        $"having names aggregates: count, or count_distinct, sum, avg, min or max with a column inside, not \"{name}\".");
            }
            return all.Count switch { 0 => null, 1 => all[0], _ => new TableAndFilter(all) };
        }

        /// <summary>The columns inside <c>sum: {amount: ..., joined_table: {price: ...}}</c>, each with what's under it.</summary>
        private IEnumerable<(TableField Field, GraphQLValue Value, string Label)> AggregateFields(GraphQLValue value, TableAggregateFunction function, string name)
        {
            if (value is not GraphQLObjectValue { Fields: { Count: > 0 } columns })
                throw new TableQueryException($"{name} takes the column inside it, such as {name}: {{amount: ...}}.");
            foreach (GraphQLObjectField c in columns)
            {
                string? header = Header(c.Name.StringValue, inJoined: false);
                if (header is not null)
                {
                    yield return (new TableField(header, function), c.Value, $"{name}.{c.Name.StringValue}");
                    continue;
                }
                if (c.Value is not GraphQLObjectValue { Fields: { } inner })
                    throw new TableQueryException($"{c.Name.StringValue} takes its columns inside it.");
                foreach (GraphQLObjectField j in inner)
                    yield return (new TableField(Header(j.Name.StringValue, inJoined: true), function), j.Value, $"{name}.{j.Name.StringValue}");
            }
        }

        /// <summary>
        /// An order: <c>{amount: desc}</c> or a list of them; in an aggregate also <c>{count: desc}</c> and
        /// <c>{sum: {amount: desc}}</c>.
        /// </summary>
        public IReadOnlyList<TableOrder>? Order(GraphQLValue? value, bool aggregate)
        {
            if (value is null)
                return null;
            List<TableOrder> orders = [];
            foreach (GraphQLValue item in List(value))
            {
                if (item is not GraphQLObjectValue obj)
                    throw new TableQueryException("order_by takes an object or a list of them, such as {amount: desc}.");
                foreach (GraphQLObjectField f in obj.Fields ?? [])
                {
                    string name = f.Name.StringValue;
                    if (aggregate && name == "count")
                        orders.Add(new TableOrder(new TableField(null, TableAggregateFunction.Count), Direction(f.Value)));
                    else if (aggregate && Function(name) is { } function && f.Value is GraphQLObjectValue)
                    {
                        foreach ((TableField field, GraphQLValue dir, _) in AggregateFields(f.Value, function, name))
                            orders.Add(new TableOrder(field, Direction(dir)));
                    }
                    else if (Header(name, inJoined: false) is { } header)
                        orders.Add(new TableOrder(new TableField(header), Direction(f.Value)));
                    else if (f.Value is GraphQLObjectValue { Fields: { } inner })
                    {
                        foreach (GraphQLObjectField j in inner)
                            orders.Add(new TableOrder(new TableField(Header(j.Name.StringValue, inJoined: true)), Direction(j.Value)));
                    }
                    else
                        throw new TableQueryException($"Order the joined table {name} by its columns, such as {name}: {{price: desc}}.");
                }
            }
            return orders;
        }

        private static TableSortDirection Direction(GraphQLValue value) => Literal(value) switch
        {
            string s when s.Equals("asc", StringComparison.OrdinalIgnoreCase) => TableSortDirection.Ascending,
            string s when s.Equals("desc", StringComparison.OrdinalIgnoreCase) => TableSortDirection.Descending,
            _ => throw new TableQueryException("An order is asc or desc."),
        };

        private static IEnumerable<GraphQLValue> List(GraphQLValue value) =>
            value is GraphQLListValue list ? list.Values ?? [] : [value];
    }

    private static TableAggregateFunction? Function(string name) => name switch
    {
        "count_distinct" => TableAggregateFunction.CountDistinct,
        "sum" => TableAggregateFunction.Sum,
        "avg" => TableAggregateFunction.Avg,
        "min" => TableAggregateFunction.Min,
        "max" => TableAggregateFunction.Max,
        _ => null,
    };

    /// <summary>The arguments of a field, refusing any not in <paramref name="allowed"/>.</summary>
    private sealed class Arguments
    {
        private readonly Dictionary<string, GraphQLValue> _values = new(StringComparer.Ordinal);

        public Arguments(GraphQLField field, params string[] allowed)
        {
            foreach (GraphQLArgument argument in field.Arguments?.Items ?? [])
            {
                string name = argument.Name.StringValue;
                if (!allowed.Contains(name))
                    throw new TableQueryException($"{field.Name.StringValue} takes no argument \"{name}\"; it takes {string.Join(", ", allowed)}.");
                if (!_values.TryAdd(name, argument.Value))
                    throw new TableQueryException($"{field.Name.StringValue} has \"{name}\" twice.");
            }
        }

        public GraphQLValue? Get(string name) => _values.GetValueOrDefault(name);
    }

    private static void NoArguments(GraphQLField field)
    {
        if (field.Arguments is { Count: > 0 })
            throw new TableQueryException($"{field.Name.StringValue} takes no arguments.");
    }

    private static int Limit(GraphQLValue? value)
    {
        int limit = Int(value, "limit") ?? DefaultLimit;
        return limit is >= 1 and <= MaxLimit ? limit : throw new TableQueryException($"limit must be between 1 and {MaxLimit}.");
    }

    private static int? Int(GraphQLValue? value, string name) => value is null ? null
        : Literal(value) is long l && l is >= 0 and <= int.MaxValue ? (int)l
        : throw new TableQueryException($"{name} takes a whole number of 0 or more.");

    private static DateTimeOffset? SeenAsOf(GraphQLValue? value) => value is null ? null
        : DateTimeOffset.TryParse(Text(value, "seen_as_of"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at) ? at
        : throw new TableQueryException("seen_as_of takes an ISO 8601 time, such as \"2026-01-31T14:00:00Z\".");

    private static string Text(GraphQLValue value, string name) => Literal(value) as string
        ?? throw new TableQueryException($"{name} takes a name or a string.");

    /// <summary>A literal as a CLR value: numbers as long or decimal, enum values as their names, lists as lists.</summary>
    internal static object? Literal(GraphQLValue value) => value switch
    {
        GraphQLNullValue => null,
        GraphQLBooleanValue b => b.BoolValue,
        GraphQLIntValue i => long.TryParse(i.Value.Span, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l) ? (object)l
            : decimal.TryParse(i.Value.Span, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out decimal big) ? (object)big
            : throw new TableQueryException("A number in the query is too large."),
        GraphQLFloatValue f => decimal.TryParse(f.Value.Span, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d) ? d
            : throw new TableQueryException("A number in the query is too large."),
        GraphQLStringValue s => s.Value.ToString(),
        GraphQLEnumValue e => e.Name.StringValue,
        GraphQLListValue list => (list.Values ?? []).Select(Literal).ToList(),
        GraphQLObjectValue => throw new TableQueryException("An object can't be compared with a column; compare with a value."),
        _ => throw new TableQueryException("Variables aren't supported; write values into the query."),
    };

    /// <summary>
    /// A key for matching parent and child values: numbers compare by value whatever their CLR type, so a whole
    /// number column can be matched to a decimal one.
    /// </summary>
    private static string Normal(object? value) => value switch
    {
        null => "",
        long or int or short or decimal => "n:" + (Convert.ToDecimal(value, CultureInfo.InvariantCulture) / 1.0000000000000000000000000000m)
            .ToString(CultureInfo.InvariantCulture),
        double x => "n:" + x.ToString("R", CultureInfo.InvariantCulture),
        _ => "s:" + Convert.ToString(Cell(value, null)?.ToJsonString(), CultureInfo.InvariantCulture),
    };

    /// <summary>A cell as JSON: dates as ISO text and a JSON column's value as JSON rather than text.</summary>
    internal static JsonNode? Cell(object? value, TableColumnType? type) => value switch
    {
        null => null,
        string json when type == TableColumnType.Json => ParseJson(json),
        string s => JsonValue.Create(s),
        long l => JsonValue.Create(l),
        int i => JsonValue.Create(i),
        short s => JsonValue.Create(s),
        decimal d => JsonValue.Create(d),
        double x => double.IsFinite(x) ? JsonValue.Create(x) : JsonValue.Create(x.ToString(CultureInfo.InvariantCulture)),
        float x => double.IsFinite(x) ? JsonValue.Create(x) : JsonValue.Create(x.ToString(CultureInfo.InvariantCulture)),
        bool b => JsonValue.Create(b),
        DateOnly d => JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        DateTime t => JsonValue.Create(t.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)),
        DateTimeOffset t => JsonValue.Create(t.ToString("o", CultureInfo.InvariantCulture)),
        Guid g => JsonValue.Create(g.ToString()),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    private static JsonNode? ParseJson(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return JsonValue.Create(json); // a cut cell isn't valid JSON any more; return its text
        }
    }
}
