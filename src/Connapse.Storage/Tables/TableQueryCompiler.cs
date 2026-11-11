using System.Globalization;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using Npgsql;
using NpgsqlTypes;

namespace Connapse.Storage.Tables;

/// <summary>
/// One compiled table query: a single <c>SELECT</c> whose only identifiers are the catalog's generated names and whose
/// every value is a positional (<c>$n</c>) parameter. <see cref="Columns"/> names the output columns as the agent knows
/// them; when it is null the runner takes the names the database returns. The query fetches <see cref="Limit"/> + 1
/// rows so the runner can tell the answer was cut. <see cref="Schema"/>, when set, is searched after <c>pg_catalog</c>
/// so the query can name that schema's tables unqualified; agent-written SQL uses it (CON-326).
/// </summary>
internal sealed record CompiledTableQuery(
    string Sql,
    IReadOnlyList<NpgsqlParameter> Parameters,
    IReadOnlyList<string>? Columns,
    int Limit,
    string? Schema = null);

/// <summary>
/// Compiles structured table requests to SQL (CON-303). Agents never write SQL: a request names columns by their
/// original headers and picks operators and aggregates from fixed lists, and this class emits the SQL for them. Names
/// are resolved against the table's catalog entry and only the generated <c>c_&lt;n&gt;</c> names, quoted, reach the
/// SQL; values are bound as typed parameters. There are no function calls an agent can choose and no subqueries; a
/// read or an aggregate may join one other table on two named columns of the same type (CON-305). Because every query binds at least its limit as a positional parameter, Npgsql sends the text unsplit,
/// so it can only ever be one statement.
/// <para>
/// Columns an admin hid (CON-304) are left out of the lookup altogether, so naming one anywhere fails exactly as a
/// column that doesn't exist does, and <c>SELECT</c> of all columns returns only the visible ones. A table merged from
/// several documents is always filtered to the rows of <c>readableSources</c>, the source documents the caller may
/// read, ahead of the caller's own filter; row-level security checks the same thing again in the database.
/// </para>
/// </summary>
internal static class TableQueryCompiler
{
    internal const int DefaultLimit = 100;
    internal const int MaxLimit = 1024;
    internal const int MaxOffset = 1_000_000;

    /// <summary>Longest text cell returned; the query fetches one more character so the runner can mark the cut.</summary>
    internal const int MaxCellCharacters = 2000;

    internal const int MaxFilterDepth = 16;
    internal const int MaxConditions = 100;
    internal const int MaxInValues = 1000;
    internal const int MaxListItems = 32;
    internal const int MaxValueLength = 10_000;

    private const int MaxNamesInError = 50;

    /// <summary>How many sample rows <see cref="CompileSample"/> returns.</summary>
    internal const int SampleRows = 3;

    /// <summary>
    /// Compiles a read. A request with a <see cref="TableReadRequest.Join"/> needs the joined table's catalog entry
    /// (<paramref name="joined"/>) and, when it is merged, the caller's readable sources of it.
    /// </summary>
    internal static CompiledTableQuery CompileRead(
        TableInfo table,
        TableReadRequest request,
        IReadOnlyList<Guid>? readableSources = null,
        TableInfo? joined = null,
        IReadOnlyList<Guid>? joinedSources = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(request);
        var query = new QueryBuilder(table, readableSources, request.Join, joined, joinedSources);

        IReadOnlyList<ColumnRef> columns = request.Columns is null || request.Columns.Count == 0
            ? query.AllVisible()
            : request.Columns.Count <= TableDdl.MaxColumns
                ? request.Columns.Select(query.Resolve).ToList()
                : throw new TableQueryException($"A read can return at most {TableDdl.MaxColumns} columns.");

        var sql = new StringBuilder("SELECT ");
        sql.AppendJoin(", ", columns.Select(c => Output(c.Expression, c.Type)));
        sql.Append(" FROM ").Append(query.From(request.SeenAsOf));
        query.AppendWhere(sql, request.Where, query.Seen(request.SeenAsOf));

        if (request.OrderBy is { Count: > 0 } orderBy)
        {
            CheckCount(orderBy.Count, "order_by entries");
            sql.Append(" ORDER BY ").AppendJoin(", ", orderBy.Select(o =>
            {
                if (o.Field.Aggregate is not null)
                    throw new TableQueryException("A read can't order by an aggregate; use an aggregate request.");
                return query.Resolve(RequireColumn(o.Field)).Expression + Direction(o.Direction);
            }));
        }

        int limit = Limit(request.Limit);
        int offset = request.Offset ?? 0;
        if (offset < 0 || offset > MaxOffset)
            throw new TableQueryException($"offset must be between 0 and {MaxOffset}.");
        sql.Append(" LIMIT ").Append(query.Parameter((long)limit + 1, NpgsqlDbType.Bigint));
        sql.Append(" OFFSET ").Append(query.Parameter((long)offset, NpgsqlDbType.Bigint));

        return new CompiledTableQuery(sql.ToString(), query.Parameters, columns.Select(c => c.Name).ToList(), limit);
    }

    /// <summary>Compiles an aggregate; a join works as it does for <see cref="CompileRead"/>.</summary>
    internal static CompiledTableQuery CompileAggregate(
        TableInfo table,
        TableAggregateRequest request,
        IReadOnlyList<Guid>? readableSources = null,
        TableInfo? joined = null,
        IReadOnlyList<Guid>? joinedSources = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(request);
        var query = new QueryBuilder(table, readableSources, request.Join, joined, joinedSources);

        if (request.Aggregates is null || request.Aggregates.Count == 0)
            throw new TableQueryException("An aggregate request needs at least one aggregate, such as {\"aggregate\": \"count\"}.");
        CheckCount(request.Aggregates.Count, "aggregates");

        IReadOnlyList<ColumnRef> groupBy = request.GroupBy is { Count: > 0 } names
            ? names.Count <= MaxListItems
                ? names.Select(query.Resolve).Distinct().ToList()
                : throw new TableQueryException($"A request can have at most {MaxListItems} group_by columns.")
            : [];

        var outputs = new List<string>();
        var labels = new List<string>();
        foreach (ColumnRef column in groupBy)
        {
            outputs.Add(Output(column.Expression, column.Type));
            labels.Add(column.Name);
        }
        foreach (TableField field in request.Aggregates)
        {
            (string expression, TableColumnType type) = query.Aggregate(field);
            outputs.Add(Output(expression, type));
            labels.Add(Label(field));
        }

        var sql = new StringBuilder("SELECT ").AppendJoin(", ", outputs).Append(" FROM ").Append(query.From(request.SeenAsOf));
        query.AppendWhere(sql, request.Where, query.Seen(request.SeenAsOf));
        if (groupBy.Count > 0)
            sql.Append(" GROUP BY ").AppendJoin(", ", groupBy.Select(c => c.Expression));
        if (request.Having is not null)
            sql.Append(" HAVING ").Append(query.Filter(request.Having, aggregates: true));

        if (request.OrderBy is { Count: > 0 } orderBy)
        {
            CheckCount(orderBy.Count, "order_by entries");
            sql.Append(" ORDER BY ").AppendJoin(", ", orderBy.Select(o =>
            {
                if (o.Field.Aggregate is not null)
                    return query.Aggregate(o.Field).Expression + Direction(o.Direction);
                ColumnRef column = query.Resolve(RequireColumn(o.Field));
                if (!groupBy.Contains(column))
                    throw new TableQueryException(
                        $"Can't order by \"{column.Name}\": it isn't in group_by. Order by a group_by column or an aggregate.");
                return column.Expression + Direction(o.Direction);
            }));
        }

        int limit = Limit(request.Limit);
        sql.Append(" LIMIT ").Append(query.Parameter((long)limit + 1, NpgsqlDbType.Bigint));

        return new CompiledTableQuery(sql.ToString(), query.Parameters, labels, limit);
    }

    /// <summary>
    /// The net change between two times in a table with history (CON-306): the versions current at
    /// <see cref="TableChangesRequest.Since"/> and at <see cref="TableChangesRequest.Until"/> (or now) joined on the key,
    /// keeping the keys that went away, came in, or differ in a chosen column. Each output row says its
    /// <c>change</c> (added, removed or changed) and <c>version</c> (old or new).
    /// </summary>
    internal static CompiledTableQuery CompileChanges(TableInfo table, TableChangesRequest request, IReadOnlyList<Guid>? readableSources = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(request);
        if (MemoryTable.Is(table))
            return CompileMemoryChanges(table, request);
        var query = new QueryBuilder(table, readableSources);
        int key = query.HistoryKey("ask what changed");
        if (request.Until is DateTimeOffset until && until < request.Since)
            throw new TableQueryException("until can't be earlier than since.");

        List<int> columns = request.Columns is null || request.Columns.Count == 0
            ? [.. table.VisibleColumnIndexes]
            : request.Columns.Count <= TableDdl.MaxColumns
                ? request.Columns.Select(c => query.Resolve(c).Index).Distinct().ToList()
                : throw new TableQueryException($"A changes request can return at most {TableDdl.MaxColumns} columns.");
        if (!columns.Contains(key) && !table.IsHidden(key))
            columns.Insert(0, key);

        string old = query.Readable(query.Seen(request.Since)!);
        string @new = query.Readable(query.Seen(request.Until) ?? query.Current());
        string selected = string.Join(", ", columns.Append(key).Distinct().Select(query.Column));
        string k = query.Column(key);
        string differs = $"ROW({string.Join(", ", columns.Select(i => "o." + query.Column(i)))}) IS DISTINCT FROM "
            + $"ROW({string.Join(", ", columns.Select(i => "n." + query.Column(i)))})";

        string Side(string alias, string other, string version, string gone, int side) =>
            $"SELECT CASE WHEN {other}.{k} IS NULL THEN '{gone}' ELSE 'changed' END AS change, '{version}' AS version, "
            + $"{side.ToString(CultureInfo.InvariantCulture)} AS side, {alias}.{k} AS sort_key, "
            + string.Join(", ", columns.Select((c, n) => $"{alias}.{query.Column(c)} AS v_{n.ToString(CultureInfo.InvariantCulture)}"))
            + $" FROM {(alias == "o" ? "old" : "new")} AS {alias} LEFT JOIN {(alias == "o" ? "new" : "old")} AS {other} ON {other}.{k} = {alias}.{k}"
            + $" WHERE {other}.{k} IS NULL OR {differs}";

        var sql = new StringBuilder("WITH old AS (SELECT ").Append(selected).Append(" FROM ").Append(query.Table)
            .Append(" WHERE ").Append(old).Append("), new AS (SELECT ").Append(selected).Append(" FROM ").Append(query.Table)
            .Append(" WHERE ").Append(@new).Append(") SELECT d.change, d.version, ")
            .AppendJoin(", ", columns.Select((c, n) => Output("d.v_" + n.ToString(CultureInfo.InvariantCulture), table.Columns[c].Type)))
            .Append(" FROM (").Append(Side("o", "n", "old", "removed", 0)).Append(" UNION ALL ")
            .Append(Side("n", "o", "new", "added", 1)).Append(") AS d ORDER BY d.sort_key, d.side");

        int limit = Limit(request.Limit);
        int offset = request.Offset ?? 0;
        if (offset < 0 || offset > MaxOffset)
            throw new TableQueryException($"offset must be between 0 and {MaxOffset}.");
        sql.Append(" LIMIT ").Append(query.Parameter((long)limit + 1, NpgsqlDbType.Bigint));
        sql.Append(" OFFSET ").Append(query.Parameter((long)offset, NpgsqlDbType.Bigint));

        var labels = new List<string> { "change", "version" };
        labels.AddRange(columns.Select(i => table.Columns[i].Name));
        return new CompiledTableQuery(sql.ToString(), query.Parameters, labels, limit);
    }

    /// <summary>
    /// What changed in a memory table (CON-309), by memory's own rule: every version, corrected ones included, that
    /// started or stopped being true between <see cref="TableChangesRequest.Since"/> and
    /// <see cref="TableChangesRequest.Until"/> (or now), ordered as memory's own changes tool orders them. Each row says
    /// its <c>change</c>: started, ended, or started and ended.
    /// </summary>
    private static CompiledTableQuery CompileMemoryChanges(TableInfo table, TableChangesRequest request)
    {
        if (request.Until is DateTimeOffset until && until < request.Since)
            throw new TableQueryException("until can't be earlier than since.");
        var query = new QueryBuilder(table, readableSources: null);

        List<int> columns = request.Columns is null || request.Columns.Count == 0
            ? [.. table.VisibleColumnIndexes]
            : request.Columns.Count <= TableDdl.MaxColumns
                ? request.Columns.Select(c => query.Resolve(c).Index).Distinct().ToList()
                : throw new TableQueryException($"A changes request can return at most {TableDdl.MaxColumns} columns.");

        string since = query.Parameter(request.Since.UtcDateTime, NpgsqlDbType.TimestampTz);
        string to = request.Until is DateTimeOffset u
            ? query.Parameter(u.UtcDateTime, NpgsqlDbType.TimestampTz)
            : "pg_catalog.statement_timestamp()";
        string from = TableDdl.Quote(TableDdl.MemoryValidFrom);
        string end = TableDdl.Quote(TableDdl.MemoryValidTo);
        string started = $"({from} >= {since} AND {from} <= {to})";
        string ended = $"({end} >= {since} AND {end} <= {to})";

        var sql = new StringBuilder("SELECT CASE WHEN ").Append(started).Append(" AND ").Append(ended)
            .Append(" THEN 'started and ended' WHEN ").Append(started).Append(" THEN 'started' ELSE 'ended' END AS change");
        foreach (int c in columns)
            sql.Append(", ").Append(Output(query.Column(c), table.Columns[c].Type));
        sql.Append(" FROM ").Append(query.Table).Append(" WHERE ").Append(started).Append(" OR ").Append(ended)
            .Append(" ORDER BY ").Append(from).Append(" ASC NULLS LAST, ").Append(query.Column(7)).Append(" ASC, ")
            .Append(query.Column(0)).Append(" ASC");

        int limit = Limit(request.Limit);
        int offset = request.Offset ?? 0;
        if (offset < 0 || offset > MaxOffset)
            throw new TableQueryException($"offset must be between 0 and {MaxOffset}.");
        sql.Append(" LIMIT ").Append(query.Parameter((long)limit + 1, NpgsqlDbType.Bigint));
        sql.Append(" OFFSET ").Append(query.Parameter((long)offset, NpgsqlDbType.Bigint));

        var labels = new List<string> { "change" };
        labels.AddRange(columns.Select(i => table.Columns[i].Name));
        return new CompiledTableQuery(sql.ToString(), query.Parameters, labels, limit);
    }

    /// <summary>
    /// The count of the rows the caller may read, as a single <c>count</c> cell. Row-level security and, for a merged
    /// table, the compiler's own filter both apply, so it never counts a row the caller can't read.
    /// </summary>
    internal static CompiledTableQuery CompileCount(TableInfo table, IReadOnlyList<Guid>? readableSources = null) =>
        CompileAggregate(table, new TableAggregateRequest(table.Id, [new TableField(null, TableAggregateFunction.Count)]), readableSources);

    /// <summary>
    /// A few rows to show what the table holds (CON-304): visible columns only, from rows the caller may read, ordered
    /// by their own contents (source document first in a merged table) so which rows are picked never depends on rows
    /// the caller can't see. An unordered <c>LIMIT</c> would take whatever the scan reached first, which a hidden row
    /// can change.
    /// </summary>
    internal static CompiledTableQuery CompileSample(TableInfo table, IReadOnlyList<Guid>? readableSources = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        var query = new QueryBuilder(table, readableSources);
        IReadOnlyList<int> columns = table.VisibleColumnIndexes;

        var sql = new StringBuilder("SELECT ");
        sql.AppendJoin(", ", columns.Select(i => Output(query.Column(i), table.Columns[i].Type)));
        sql.Append(" FROM ").Append(query.Table);
        query.AppendWhere(sql, where: null, query.Seen(asOf: null));

        IEnumerable<string> order = columns.Select(i => query.Column(i) + " ASC NULLS LAST");
        if (table.IsMerged)
            order = order.Prepend(TableDdl.Quote(TableDdl.DocumentColumn) + " ASC");
        if (columns.Count > 0 || table.IsMerged)
            sql.Append(" ORDER BY ").AppendJoin(", ", order);
        sql.Append(" LIMIT ").Append(query.Parameter((long)SampleRows + 1, NpgsqlDbType.Bigint));

        return new CompiledTableQuery(sql.ToString(), query.Parameters, columns.Select(i => table.Columns[i].Name).ToList(), SampleRows);
    }

    /// <summary>The answer column's name for an aggregate, such as <c>count</c> or <c>sum(amount)</c>.</summary>
    internal static string Label(TableField field) =>
        (field.Aggregate is { } function ? FunctionName(function) : "value")
        + (field.Column is null ? "" : "(" + field.Column + ")");

    internal static string FunctionName(TableAggregateFunction function) => function switch
    {
        TableAggregateFunction.Count => "count",
        TableAggregateFunction.CountDistinct => "count_distinct",
        TableAggregateFunction.Sum => "sum",
        TableAggregateFunction.Avg => "avg",
        TableAggregateFunction.Min => "min",
        TableAggregateFunction.Max => "max",
        _ => throw new ArgumentOutOfRangeException(nameof(function), function, null),
    };

    /// <summary>Text output is cut one character past the cap, so the runner can shorten it and say so.</summary>
    private static string Output(string expression, TableColumnType type) =>
        type == TableColumnType.Text
            ? $"pg_catalog.substr({expression}, 1, {(MaxCellCharacters + 1).ToString(CultureInfo.InvariantCulture)})"
            : expression;

    private static string Direction(TableSortDirection direction) =>
        direction == TableSortDirection.Descending ? " DESC NULLS LAST" : " ASC NULLS LAST";

    private static int Limit(int? requested)
    {
        int limit = requested ?? DefaultLimit;
        return limit is >= 1 and <= MaxLimit
            ? limit
            : throw new TableQueryException($"limit must be between 1 and {MaxLimit}.");
    }

    private static void CheckCount(int count, string what)
    {
        if (count > MaxListItems)
            throw new TableQueryException($"A request can have at most {MaxListItems} {what}.");
    }

    private static string RequireColumn(TableField field) =>
        field.Column ?? throw new TableQueryException("Each entry needs a \"column\".");

    /// <summary>
    /// A column an agent named, as the SQL writes it (<see cref="Expression"/>) and as the answer labels it
    /// (<see cref="Name"/>: the header, or <c>&lt;as&gt;.&lt;header&gt;</c> for a joined table's column).
    /// </summary>
    private readonly record struct ColumnRef(string Expression, TableColumnType Type, string Name, int Index);

    /// <summary>
    /// Holds one query's tables (the queried one and at most one joined one), their column lookups and the parameters
    /// bound so far. Without a join the SQL names columns unqualified; with one, the queried table is <c>t</c> and the
    /// joined table <c>j</c>.
    /// </summary>
    private sealed class QueryBuilder
    {
        private readonly TableSide _base;
        private readonly TableSide? _joined;
        private readonly TableJoin? _join;
        private readonly List<NpgsqlParameter> _parameters = [];
        private int _conditions;

        internal QueryBuilder(
            TableInfo table,
            IReadOnlyList<Guid>? readableSources,
            TableJoin? join = null,
            TableInfo? joined = null,
            IReadOnlyList<Guid>? joinedSources = null)
        {
            _base = new TableSide(this, table, readableSources, join is null ? null : "t", prefix: null);
            if (join is not null)
            {
                if (joined is null || joined.Id != join.TableId)
                    throw new InvalidOperationException($"A join to table {join.TableId} needs that table's catalog entry.");
                if (join.As is null || !TableRequestJson.IsJoinAlias(join.As))
                    throw new TableQueryException(
                        "A join needs \"as\": a short name for the joined table, starting with a letter, with only letters, digits and _ (at most 32 characters), such as \"c\".");
                _joined = new TableSide(this, joined, joinedSources, "j", join.As);
                _join = join;
                _base.Hint = $" Columns of the joined table \"{joined.Name}\" are named \"{join.As}.<header>\".";
            }
            Table = _base.Table;
        }

        internal IReadOnlyList<NpgsqlParameter> Parameters => _parameters;

        internal string Table { get; }

        /// <summary>
        /// The <c>FROM</c> clause's contents. With a join, the joined table's readable-source filter and the versions it
        /// shows go in the <c>ON</c> clause, so in an inner join a row the caller can't read matches nothing and in a
        /// left join it shows as empty, exactly as a row that isn't there.
        /// </summary>
        internal string From(DateTimeOffset? seenAsOf)
        {
            if (_join is null || _joined is null)
                return Table;

            ColumnRef left = _base.Resolve(_join.Column);
            ColumnRef right = _joined.Resolve(_join.JoinColumn);
            if (left.Type != right.Type)
                throw new TableQueryException(
                    $"Can't join \"{left.Name}\", a {TypeName(left.Type)} column, to \"{right.Name}\", a {TypeName(right.Type)} column: join columns must have the same type.");

            var on = new List<string> { left.Expression + " = " + right.Expression };
            if (_joined.Info.IsMerged)
                on.Add(_joined.SourceFilter());
            if (_joined.Seen(seenAsOf, strict: false) is { } seen)
                on.Add(seen);
            return _base.From
                + (_join.Kind == TableJoinKind.Left ? " LEFT JOIN " : " INNER JOIN ")
                + _joined.From + " ON " + string.Join(" AND ", on);
        }

        /// <summary>Every visible column: the queried table's, then the joined table's.</summary>
        internal IReadOnlyList<ColumnRef> AllVisible()
        {
            List<ColumnRef> columns = [.. _base.AllVisible()];
            if (_joined is not null)
                columns.AddRange(_joined.AllVisible());
            return columns.Count <= TableDdl.MaxColumns
                ? columns
                : throw new TableQueryException(
                    $"The two tables have {columns.Count} visible columns together, more than a read can return ({TableDdl.MaxColumns}). Name the columns you want in \"columns\".");
        }

        /// <summary>
        /// Appends the <c>WHERE</c> clause: a merged table's readable-source filter first, then the caller's filter,
        /// so the caller's filter can only narrow what the caller may read.
        /// </summary>
        internal void AppendWhere(StringBuilder sql, TableFilter? where, string? seen = null)
        {
            var conditions = new List<string>();
            if (_base.Info.IsMerged)
                conditions.Add(_base.SourceFilter());
            if (seen is not null)
                conditions.Add(seen);
            if (where is not null)
                conditions.Add(conditions.Count > 0 ? "(" + Filter(where, aggregates: false) + ")" : Filter(where, aggregates: false));
            if (conditions.Count > 0)
                sql.Append(" WHERE ").AppendJoin(" AND ", conditions);
        }

        /// <summary><paramref name="condition"/>, narrowed in a merged table to the rows the caller may read.</summary>
        internal string Readable(string condition) =>
            _base.Info.IsMerged ? _base.SourceFilter() + " AND (" + condition + ")" : condition;

        internal string Column(int index) => _base.Column(index);

        /// <summary>
        /// The column an agent named: the queried table's by its header, or with a join, the joined table's as
        /// <c>&lt;as&gt;.&lt;header&gt;</c>. Anything else is an error listing the columns, so the agent can retry.
        /// </summary>
        internal ColumnRef Resolve(string name)
        {
            if (_joined is not null && name is not null
                && name.Length > _joined.Prefix!.Length + 1
                && name.StartsWith(_joined.Prefix + ".", StringComparison.OrdinalIgnoreCase))
            {
                if (_base.HasExact(name))
                    throw new TableQueryException(
                        $"\"{name}\" is ambiguous: it is a column of \"{_base.Info.Name}\" and also names a column of the joined table. Pick a different \"as\" for the join.");
                return _joined.Resolve(name[(_joined.Prefix.Length + 1)..]);
            }
            return _base.Resolve(name!);
        }

        /// <summary>
        /// Which versions a read sees: for a table with history, those current at <paramref name="asOf"/>, or the
        /// current ones when it is null. Null for a table without history, whose rows are all current.
        /// </summary>
        internal string? Seen(DateTimeOffset? asOf) => _base.Seen(asOf, strict: true);

        internal string Current() => _base.Current();

        /// <summary>The key column of a table with history, or an error saying the table keeps none.</summary>
        internal int HistoryKey(string what) => _base.HistoryKey(what);

        internal string Parameter(object value, NpgsqlDbType type)
        {
            _parameters.Add(new NpgsqlParameter { Value = value, NpgsqlDbType = type });
            return "$" + _parameters.Count.ToString(CultureInfo.InvariantCulture);
        }

        internal (string Expression, TableColumnType Type) Aggregate(TableField field)
        {
            TableAggregateFunction function = field.Aggregate
                ?? throw new TableQueryException("Each aggregate needs an \"aggregate\": count, count_distinct, sum, avg, min or max.");

            if (field.Column is null)
            {
                return function == TableAggregateFunction.Count
                    ? ("pg_catalog.count(*)", TableColumnType.WholeNumber)
                    : throw new TableQueryException($"{FunctionName(function)} needs a \"column\".");
            }

            ColumnRef target = Resolve(field.Column);
            string column = target.Expression;
            TableColumnType type = target.Type;
            return function switch
            {
                TableAggregateFunction.Count => ($"pg_catalog.count({column})", TableColumnType.WholeNumber),
                TableAggregateFunction.CountDistinct => ($"pg_catalog.count(DISTINCT {column})", TableColumnType.WholeNumber),
                TableAggregateFunction.Sum or TableAggregateFunction.Avg when type is TableColumnType.WholeNumber or TableColumnType.Number =>
                    ($"pg_catalog.{FunctionName(function)}({column})", TableColumnType.Number),
                TableAggregateFunction.Min or TableAggregateFunction.Max when type != TableColumnType.Boolean =>
                    ($"pg_catalog.{FunctionName(function)}({column})", type),
                _ => throw new TableQueryException(
                    $"{FunctionName(function)} doesn't work on \"{target.Name}\", a {TypeName(type)} column."),
            };
        }

        internal string Filter(TableFilter filter, bool aggregates) => Filter(filter, aggregates, depth: 1);

        private string Filter(TableFilter filter, bool aggregates, int depth)
        {
            if (depth > MaxFilterDepth)
                throw new TableQueryException($"A filter can nest at most {MaxFilterDepth} levels deep.");

            return filter switch
            {
                TableAndFilter and => Group(and.All, " AND ", "and", aggregates, depth),
                TableOrFilter or => Group(or.Any, " OR ", "or", aggregates, depth),
                TableNotFilter not => "NOT (" + Filter(not.Inner ?? throw new TableQueryException("\"not\" needs a filter."), aggregates, depth + 1) + ")",
                TableCondition condition => Condition(condition, aggregates),
                null => throw new TableQueryException("A filter can't be null."),
                _ => throw new TableQueryException("Unknown filter."),
            };
        }

        private string Group(IReadOnlyList<TableFilter>? filters, string separator, string name, bool aggregates, int depth)
        {
            if (filters is null || filters.Count == 0)
                throw new TableQueryException($"\"{name}\" needs at least one filter.");
            return "(" + string.Join(separator, filters.Select(f => Filter(f, aggregates, depth + 1))) + ")";
        }

        private string Condition(TableCondition condition, bool aggregates)
        {
            if (++_conditions > MaxConditions)
                throw new TableQueryException($"A request can have at most {MaxConditions} conditions.");

            string expression;
            TableColumnType type;
            string subject;
            if (condition.Field.Aggregate is not null)
            {
                if (!aggregates)
                    throw new TableQueryException("\"where\" filters rows, so it can't use an aggregate; put it in \"having\".");
                (expression, type) = Aggregate(condition.Field);
                subject = Label(condition.Field);
            }
            else
            {
                if (aggregates)
                    throw new TableQueryException("\"having\" filters groups, so each condition needs an \"aggregate\"; filter rows in \"where\".");
                ColumnRef column = Resolve(RequireColumn(condition.Field));
                expression = column.Expression;
                type = column.Type;
                subject = condition.Field.Column!;
            }

            IReadOnlyList<object?> values = condition.Values ?? [];
            return condition.Operator switch
            {
                TableFilterOperator.IsNull => values.Count == 0
                    ? expression + " IS NULL"
                    : throw new TableQueryException("is_null takes no value."),
                TableFilterOperator.Equal => Compare(expression, "=", values, type, subject),
                TableFilterOperator.NotEqual => Compare(expression, "<>", values, type, subject),
                TableFilterOperator.Less => Ordered(expression, "<", values, type, subject),
                TableFilterOperator.LessOrEqual => Ordered(expression, "<=", values, type, subject),
                TableFilterOperator.Greater => Ordered(expression, ">", values, type, subject),
                TableFilterOperator.GreaterOrEqual => Ordered(expression, ">=", values, type, subject),
                TableFilterOperator.Between => Between(expression, values, type, subject),
                TableFilterOperator.In => In(expression, values, type, subject),
                TableFilterOperator.Contains => Text(type, subject, "contains")
                    + $"pg_catalog.strpos(pg_catalog.lower({expression}), pg_catalog.lower({Value(One(values, "contains"), type, subject)})) > 0",
                TableFilterOperator.StartsWith => Text(type, subject, "starts_with")
                    + $"pg_catalog.starts_with({expression}, {Value(One(values, "starts_with"), type, subject)})",
                _ => throw new TableQueryException("Unknown operator."),
            };
        }

        private string Compare(string expression, string op, IReadOnlyList<object?> values, TableColumnType type, string subject) =>
            $"{expression} {op} {Value(One(values, op), type, subject)}";

        private string Ordered(string expression, string op, IReadOnlyList<object?> values, TableColumnType type, string subject) =>
            type == TableColumnType.Boolean
                ? throw new TableQueryException($"\"{op}\" doesn't work on \"{subject}\", a true/false column.")
                : Compare(expression, op, values, type, subject);

        private string Between(string expression, IReadOnlyList<object?> values, TableColumnType type, string subject)
        {
            if (values.Count != 2)
                throw new TableQueryException("between takes exactly two values: [low, high].");
            if (type == TableColumnType.Boolean)
                throw new TableQueryException($"between doesn't work on \"{subject}\", a true/false column.");
            return $"{expression} BETWEEN {Value(values[0], type, subject)} AND {Value(values[1], type, subject)}";
        }

        private string In(string expression, IReadOnlyList<object?> values, TableColumnType type, string subject)
        {
            if (values.Count == 0)
                throw new TableQueryException("in needs at least one value.");
            if (values.Count > MaxInValues)
                throw new TableQueryException($"in takes at most {MaxInValues} values.");

            Array array = Array.CreateInstance(ClrType(type), values.Count);
            for (int i = 0; i < values.Count; i++)
                array.SetValue(TableQueryValues.Convert(values[i], type, subject), i);
            return $"{expression} = ANY({Parameter(array, NpgsqlDbType.Array | TableDdl.CopyType(type))})";
        }

        private string Value(object? value, TableColumnType type, string subject) =>
            Parameter(TableQueryValues.Convert(value, type, subject), TableDdl.CopyType(type));

        private static object? One(IReadOnlyList<object?> values, string op) =>
            values.Count == 1 ? values[0] : throw new TableQueryException($"\"{op}\" takes exactly one value.");

        private static string Text(TableColumnType type, string subject, string op) =>
            type == TableColumnType.Text
                ? ""
                : throw new TableQueryException($"{op} only works on text columns, and \"{subject}\" is a {TypeName(type)} column.");
    }

    /// <summary>
    /// One table in a query: its name in the SQL (qualified by <c>alias</c> when the query joins), its column lookup over
    /// visible columns only, and its readable-source and history filters.
    /// </summary>
    private sealed class TableSide
    {
        private readonly QueryBuilder query;
        private readonly IReadOnlyList<Guid>? readableSources;
        private readonly string? alias;

        internal TableSide(QueryBuilder query, TableInfo table, IReadOnlyList<Guid>? readableSources, string? alias, string? prefix)
        {
            // A merged table compiled without the caller's readable sources would show every row: refuse it.
            if (table.IsMerged && readableSources is null)
                throw new InvalidOperationException($"Table {table.Id} is merged from several documents; its query needs the caller's readable sources.");
            this.query = query;
            this.readableSources = readableSources;
            this.alias = alias;
            Info = table;
            Prefix = prefix;
            IsMemory = MemoryTable.Is(table);
            Table = TableDdl.Quote(table.StorageSchema) + "." + TableDdl.Quote(table.StorageName);
        }

        /// <summary>A memory table (CON-309), whose versions follow memory's own time rule instead of the table history's.</summary>
        internal bool IsMemory { get; }

        internal TableInfo Info { get; }

        /// <summary>What a joined table's columns are prefixed with in a request; null for the queried table.</summary>
        internal string? Prefix { get; }

        /// <summary>Added to a missing-column error, to say how the joined table's columns are named.</summary>
        internal string Hint { get; set; } = "";

        internal string Table { get; }

        internal string From => alias is null ? Table : Table + " AS " + alias;

        private string Qualify(string column) => alias is null ? column : alias + "." + column;

        internal string Column(int index) => Qualify(TableDdl.Quote(TableDdl.ColumnName(index)));

        internal string SourceFilter() =>
            Qualify(TableDdl.Quote(TableDdl.DocumentColumn)) + " = ANY("
            + query.Parameter(readableSources!.Distinct().ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Uuid) + ")";

        internal IEnumerable<ColumnRef> AllVisible() => Info.VisibleColumnIndexes.Select(Ref);

        private ColumnRef Ref(int index) =>
            new(Column(index), Info.Columns[index].Type, Prefix is null ? Info.Columns[index].Name : Prefix + "." + Info.Columns[index].Name, index);

        internal bool HasExact(string name) => Matches(name, StringComparison.Ordinal).Length > 0;

        /// <summary>
        /// The column an agent named: an exact header match, or else the one header equal to it ignoring case. Anything
        /// else is an error listing the table's columns, so the agent can retry with a real one.
        /// </summary>
        internal ColumnRef Resolve(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new TableQueryException("A column name can't be empty. " + ColumnList());

            int[] exact = Matches(name, StringComparison.Ordinal);
            if (exact.Length == 1)
                return Ref(exact[0]);
            if (exact.Length > 1)
                throw new TableQueryException($"Column \"{Named(name)}\" is ambiguous: the table has {exact.Length} columns with that header.");

            int[] loose = Matches(name, StringComparison.OrdinalIgnoreCase);
            return loose.Length switch
            {
                1 => Ref(loose[0]),
                0 => throw new TableQueryException($"Table \"{Info.Name}\" has no column \"{Named(name)}\". " + ColumnList() + Hint),
                _ => throw new TableQueryException(
                    $"Column \"{Named(name)}\" is ambiguous: it matches {string.Join(", ", loose.Select(i => Quoted(Named(Info.Columns[i].Name))))}. Use the exact header."),
            };
        }

        private string Named(string header) => Prefix is null ? header : Prefix + "." + header;

        /// <summary>
        /// Which versions a read sees: for a table with history, those current at <paramref name="asOf"/>, or the
        /// current ones when it is null. Null for a table without history, whose rows are all current. A joined table
        /// without history (<paramref name="strict"/> false) is read as it is now whatever <paramref name="asOf"/> says.
        /// </summary>
        internal string? Seen(DateTimeOffset? asOf, bool strict)
        {
            if (IsMemory)
                return MemoryAsOf(asOf is DateTimeOffset m ? query.Parameter(m.UtcDateTime, NpgsqlDbType.TimestampTz) : null);
            if (asOf is not DateTimeOffset at)
                return Info.HasHistory ? Current() : null;
            if (!strict && !Info.HasHistory)
                return null;

            HistoryKey("read it as seen at a time; leave out seen_as_of");
            DateTime utc = at.UtcDateTime;
            if (utc < Info.HistoryFrom!.Value)
                throw new TableQueryException(
                    $"Table \"{Info.Name}\" has history from {Time(Info.HistoryFrom.Value)} on, when it last got a new key or new columns, so it can't be read as seen before then.");
            string t = query.Parameter(utc, NpgsqlDbType.TimestampTz);
            return $"{Qualify(TableDdl.Quote(TableHistory.SeenFrom))} <= {t} AND {Qualify(TableDdl.Quote(TableHistory.SeenUntil))} > {t}";
        }

        internal string Current() =>
            IsMemory ? MemoryAsOf(null) : $"{Qualify(TableDdl.Quote(TableHistory.SeenUntil))} = 'infinity'";

        /// <summary>
        /// Memory's own as-of rule: the facts not corrected and true at <paramref name="at"/> (a bound parameter), or
        /// now when it is null.
        /// </summary>
        private string MemoryAsOf(string? at)
        {
            string t = at ?? "pg_catalog.statement_timestamp()";
            string from = Qualify(TableDdl.Quote(TableDdl.MemoryValidFrom));
            string to = Qualify(TableDdl.Quote(TableDdl.MemoryValidTo));
            return $"{Qualify(TableDdl.Quote(TableDdl.MemorySupersededAt))} IS NULL AND ({from} IS NULL OR {from} <= {t}) AND ({to} IS NULL OR {to} > {t})";
        }

        /// <summary>The key column of a table with history, or an error saying the table keeps none.</summary>
        internal int HistoryKey(string what)
        {
            if (!Info.HasHistory)
                throw new TableQueryException(
                    $"Table \"{Info.Name}\" has no key column, so each sync replaces its rows and it keeps no history. It can't {what}.");
            int key = Info.Columns.Select((c, i) => (c, i)).FirstOrDefault(x => string.Equals(x.c.Name, Info.Key, StringComparison.Ordinal), (null!, -1)).i;
            return key >= 0 ? key : throw new InvalidOperationException($"Table {Info.Id}'s key column is missing.");
        }

        private static string Time(DateTime utc) =>
            DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

        /// <summary>The visible columns whose header matches; a hidden column never matches, so it reads as missing.</summary>
        private int[] Matches(string name, StringComparison comparison) =>
            Info.VisibleColumnIndexes.Where(i => string.Equals(Info.Columns[i].Name, name, comparison)).ToArray();

        private string ColumnList()
        {
            IReadOnlyList<TableColumn> visible = Info.VisibleColumns;
            IEnumerable<string> names = visible.Take(MaxNamesInError).Select(c => Quoted(Named(c.Name)));
            string more = visible.Count > MaxNamesInError ? $", and {visible.Count - MaxNamesInError} more" : "";
            return "Its columns are: " + string.Join(", ", names) + more + ".";
        }

        private static string Quoted(string name) => "\"" + name + "\"";
    }

    internal static string TypeName(TableColumnType type) => type switch
    {
        TableColumnType.Text => "text",
        TableColumnType.WholeNumber => "whole-number",
        TableColumnType.Number => "number",
        TableColumnType.Boolean => "true/false",
        TableColumnType.Date => "date",
        TableColumnType.DateTime => "date-time",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    private static Type ClrType(TableColumnType type) => type switch
    {
        TableColumnType.Text => typeof(string),
        TableColumnType.WholeNumber => typeof(long),
        TableColumnType.Number => typeof(decimal),
        TableColumnType.Boolean => typeof(bool),
        TableColumnType.Date => typeof(DateOnly),
        TableColumnType.DateTime => typeof(DateTime),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}

/// <summary>Turns a filter value from an agent (a JSON value or a CLR value) into its column's type, or refuses it.</summary>
internal static class TableQueryValues
{
    internal static object Convert(object? value, TableColumnType type, string subject)
    {
        if (value is JsonElement element)
            value = FromJson(element, subject);

        object? converted = (type, value) switch
        {
            (_, null or DBNull) => throw new TableQueryException(
                $"A comparison on \"{subject}\" needs a value; use is_null to match empty cells."),
            (TableColumnType.Text, string s) => s.Length <= TableQueryCompiler.MaxValueLength
                ? s
                : throw new TableQueryException($"A text value can be at most {TableQueryCompiler.MaxValueLength} characters."),
            (TableColumnType.WholeNumber, long or int or short or byte) => System.Convert.ToInt64(value, CultureInfo.InvariantCulture),
            (TableColumnType.WholeNumber, decimal d) when d == decimal.Truncate(d) && d is >= long.MinValue and <= long.MaxValue => (long)d,
            (TableColumnType.WholeNumber, string s) when long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l) => l,
            (TableColumnType.Number, decimal d) => d,
            (TableColumnType.Number, long or int or short or byte) => System.Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            (TableColumnType.Number, double x) when double.IsFinite(x) && Math.Abs(x) < 7.9e28 => (decimal)x,
            (TableColumnType.Number, string s) when decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d) => d,
            (TableColumnType.Boolean, bool b) => b,
            (TableColumnType.Boolean, string s) when bool.TryParse(s, out bool b) => b,
            (TableColumnType.Date, DateOnly d) => d,
            (TableColumnType.Date, DateTime d) => DateOnly.FromDateTime(d),
            (TableColumnType.Date, string s) when DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d) => d,
            (TableColumnType.DateTime, DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Unspecified),
            (TableColumnType.DateTime, string s) when DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Unspecified),
            _ => null,
        };

        return converted ?? throw new TableQueryException(
            $"\"{subject}\" is a {TableQueryCompiler.TypeName(type)} column, so it can't be compared with {Describe(value!)}.{Hint(type)}");
    }

    private static object? FromJson(JsonElement element, string subject) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out long l) => l,
        JsonValueKind.Number when element.TryGetDecimal(out decimal d) => d,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => throw new TableQueryException($"A value compared with \"{subject}\" must be a string, number, true or false."),
    };

    private static string Describe(object value) => value switch
    {
        string s => "the text \"" + (s.Length > 40 ? s[..40] + "…" : s) + "\"",
        bool b => b ? "true" : "false",
        long or int or decimal or double => "the number " + System.Convert.ToString(value, CultureInfo.InvariantCulture),
        _ => "a " + value.GetType().Name,
    };

    private static string Hint(TableColumnType type) => type switch
    {
        TableColumnType.Date => " Write dates as \"yyyy-mm-dd\".",
        TableColumnType.DateTime => " Write date-times as ISO 8601, such as \"2026-01-31T14:00:00\".",
        _ => "",
    };
}
