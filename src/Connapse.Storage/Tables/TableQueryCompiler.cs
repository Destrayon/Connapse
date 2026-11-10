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
/// rows so the runner can tell the answer was cut.
/// </summary>
internal sealed record CompiledTableQuery(
    string Sql,
    IReadOnlyList<NpgsqlParameter> Parameters,
    IReadOnlyList<string>? Columns,
    int Limit);

/// <summary>
/// Compiles structured table requests to SQL (CON-303). Agents never write SQL: a request names columns by their
/// original headers and picks operators and aggregates from fixed lists, and this class emits the SQL for them. Names
/// are resolved against the table's catalog entry and only the generated <c>c_&lt;n&gt;</c> names, quoted, reach the
/// SQL; values are bound as typed parameters. There are no function calls an agent can choose, no joins and no
/// subqueries. Because every query binds at least its limit as a positional parameter, Npgsql sends the text unsplit,
/// so it can only ever be one statement.
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

    internal static CompiledTableQuery CompileRead(TableInfo table, TableReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(request);
        var query = new QueryBuilder(table);

        IReadOnlyList<int> columns = request.Columns is null || request.Columns.Count == 0
            ? Enumerable.Range(0, table.Columns.Count).ToList()
            : request.Columns.Count <= TableDdl.MaxColumns
                ? request.Columns.Select(query.Resolve).ToList()
                : throw new TableQueryException($"A read can return at most {TableDdl.MaxColumns} columns.");

        var sql = new StringBuilder("SELECT ");
        sql.AppendJoin(", ", columns.Select(i => Output(query.Column(i), table.Columns[i].Type)));
        sql.Append(" FROM ").Append(query.Table);

        if (request.Where is not null)
            sql.Append(" WHERE ").Append(query.Filter(request.Where, aggregates: false));

        if (request.OrderBy is { Count: > 0 } orderBy)
        {
            CheckCount(orderBy.Count, "order_by entries");
            sql.Append(" ORDER BY ").AppendJoin(", ", orderBy.Select(o =>
            {
                if (o.Field.Aggregate is not null)
                    throw new TableQueryException("A read can't order by an aggregate; use an aggregate request.");
                return query.Column(query.Resolve(RequireColumn(o.Field))) + Direction(o.Direction);
            }));
        }

        int limit = Limit(request.Limit);
        int offset = request.Offset ?? 0;
        if (offset < 0 || offset > MaxOffset)
            throw new TableQueryException($"offset must be between 0 and {MaxOffset}.");
        sql.Append(" LIMIT ").Append(query.Parameter((long)limit + 1, NpgsqlDbType.Bigint));
        sql.Append(" OFFSET ").Append(query.Parameter((long)offset, NpgsqlDbType.Bigint));

        return new CompiledTableQuery(sql.ToString(), query.Parameters, columns.Select(i => table.Columns[i].Name).ToList(), limit);
    }

    internal static CompiledTableQuery CompileAggregate(TableInfo table, TableAggregateRequest request)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(request);
        var query = new QueryBuilder(table);

        if (request.Aggregates is null || request.Aggregates.Count == 0)
            throw new TableQueryException("An aggregate request needs at least one aggregate, such as {\"aggregate\": \"count\"}.");
        CheckCount(request.Aggregates.Count, "aggregates");

        IReadOnlyList<int> groupBy = request.GroupBy is { Count: > 0 } names
            ? names.Count <= MaxListItems
                ? names.Select(query.Resolve).Distinct().ToList()
                : throw new TableQueryException($"A request can have at most {MaxListItems} group_by columns.")
            : [];

        var outputs = new List<string>();
        var labels = new List<string>();
        foreach (int i in groupBy)
        {
            outputs.Add(Output(query.Column(i), table.Columns[i].Type));
            labels.Add(table.Columns[i].Name);
        }
        foreach (TableField field in request.Aggregates)
        {
            (string expression, TableColumnType type) = query.Aggregate(field);
            outputs.Add(Output(expression, type));
            labels.Add(Label(field));
        }

        var sql = new StringBuilder("SELECT ").AppendJoin(", ", outputs).Append(" FROM ").Append(query.Table);
        if (request.Where is not null)
            sql.Append(" WHERE ").Append(query.Filter(request.Where, aggregates: false));
        if (groupBy.Count > 0)
            sql.Append(" GROUP BY ").AppendJoin(", ", groupBy.Select(query.Column));
        if (request.Having is not null)
            sql.Append(" HAVING ").Append(query.Filter(request.Having, aggregates: true));

        if (request.OrderBy is { Count: > 0 } orderBy)
        {
            CheckCount(orderBy.Count, "order_by entries");
            sql.Append(" ORDER BY ").AppendJoin(", ", orderBy.Select(o =>
            {
                if (o.Field.Aggregate is not null)
                    return query.Aggregate(o.Field).Expression + Direction(o.Direction);
                int column = query.Resolve(RequireColumn(o.Field));
                if (!groupBy.Contains(column))
                    throw new TableQueryException(
                        $"Can't order by \"{table.Columns[column].Name}\": it isn't in group_by. Order by a group_by column or an aggregate.");
                return query.Column(column) + Direction(o.Direction);
            }));
        }

        int limit = Limit(request.Limit);
        sql.Append(" LIMIT ").Append(query.Parameter((long)limit + 1, NpgsqlDbType.Bigint));

        return new CompiledTableQuery(sql.ToString(), query.Parameters, labels, limit);
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

    /// <summary>Holds one query's table, its column lookup and the parameters bound so far.</summary>
    private sealed class QueryBuilder(TableInfo table)
    {
        private readonly List<NpgsqlParameter> _parameters = [];
        private int _conditions;

        internal IReadOnlyList<NpgsqlParameter> Parameters => _parameters;

        internal string Table { get; } = TableDdl.Quote(table.StorageSchema) + "." + TableDdl.Quote(table.StorageName);

        internal string Column(int index) => TableDdl.Quote(TableDdl.ColumnName(index));

        /// <summary>
        /// The index of the column an agent named: an exact header match, or else the one header equal to it ignoring
        /// case. Anything else is an error listing the table's columns, so the agent can retry with a real one.
        /// </summary>
        internal int Resolve(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new TableQueryException("A column name can't be empty. " + ColumnList());

            int[] exact = Matches(name, StringComparison.Ordinal);
            if (exact.Length == 1)
                return exact[0];
            if (exact.Length > 1)
                throw new TableQueryException($"Column \"{name}\" is ambiguous: the table has {exact.Length} columns with that header.");

            int[] loose = Matches(name, StringComparison.OrdinalIgnoreCase);
            return loose.Length switch
            {
                1 => loose[0],
                0 => throw new TableQueryException($"Table \"{table.Name}\" has no column \"{name}\". " + ColumnList()),
                _ => throw new TableQueryException(
                    $"Column \"{name}\" is ambiguous: it matches {string.Join(", ", loose.Select(i => Quoted(table.Columns[i].Name)))}. Use the exact header."),
            };
        }

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

            int index = Resolve(field.Column);
            string column = Column(index);
            TableColumnType type = table.Columns[index].Type;
            return function switch
            {
                TableAggregateFunction.Count => ($"pg_catalog.count({column})", TableColumnType.WholeNumber),
                TableAggregateFunction.CountDistinct => ($"pg_catalog.count(DISTINCT {column})", TableColumnType.WholeNumber),
                TableAggregateFunction.Sum or TableAggregateFunction.Avg when type is TableColumnType.WholeNumber or TableColumnType.Number =>
                    ($"pg_catalog.{FunctionName(function)}({column})", TableColumnType.Number),
                TableAggregateFunction.Min or TableAggregateFunction.Max when type != TableColumnType.Boolean =>
                    ($"pg_catalog.{FunctionName(function)}({column})", type),
                _ => throw new TableQueryException(
                    $"{FunctionName(function)} doesn't work on \"{table.Columns[index].Name}\", a {TypeName(type)} column."),
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
                int index = Resolve(RequireColumn(condition.Field));
                expression = Column(index);
                type = table.Columns[index].Type;
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

        private int[] Matches(string name, StringComparison comparison) =>
            table.Columns.Select((c, i) => (c, i)).Where(x => string.Equals(x.c.Name, name, comparison)).Select(x => x.i).ToArray();

        private string ColumnList()
        {
            IEnumerable<string> names = table.Columns.Take(MaxNamesInError).Select(c => Quoted(c.Name));
            string more = table.Columns.Count > MaxNamesInError ? $", and {table.Columns.Count - MaxNamesInError} more" : "";
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
