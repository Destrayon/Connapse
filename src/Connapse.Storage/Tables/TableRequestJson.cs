using System.Globalization;
using System.Text.Json;
using Connapse.Core;

namespace Connapse.Storage.Tables;

/// <summary>
/// Reads an agent's JSON table request (CON-303). The format is strict: an unknown key, a wrong kind of value or an
/// unknown operator is a <see cref="TableQueryException"/> that says what was wrong, so a typo can't silently widen
/// a query. A read looks like
/// <code>{"table": "&lt;id&gt;", "columns": ["region", "amount"], "where": {"column": "amount", "op": "&gt;", "value": 100},
///  "order_by": [{"column": "amount", "direction": "desc"}], "limit": 20, "offset": 0}</code>
/// and an aggregate like
/// <code>{"table": "&lt;id&gt;", "aggregates": [{"aggregate": "count"}, {"aggregate": "sum", "column": "amount"}],
///  "group_by": ["region"], "having": {"aggregate": "count", "op": "&gt;=", "value": 5},
///  "order_by": [{"aggregate": "sum", "column": "amount", "direction": "desc"}], "limit": 10}</code>
/// Filters combine with <c>{"and": [...]}</c>, <c>{"or": [...]}</c> and <c>{"not": {...}}</c>. A condition has
/// <c>op</c> one of <c>= != &lt; &lt;= &gt; &gt;= in between is_null contains starts_with</c>, a <c>value</c>, or
/// <c>values</c> for <c>in</c> and <c>between</c>.
/// <para>
/// A read or an aggregate of a table with history (CON-306) can add <c>"seen_as_of": "2026-11-01T09:00:00Z"</c> to see
/// the table as Connapse saw it then. What changed between two times looks like
/// <code>{"table": "&lt;id&gt;", "since": "2026-11-01T00:00:00Z", "until": "2026-11-08T00:00:00Z", "columns": ["price"],
///  "limit": 50, "offset": 0}</code>
/// with <c>until</c> optional (now). Times are ISO 8601; one without an offset is UTC.
/// </para>
/// <para>
/// A read or an aggregate can join one other table of the same container (CON-305) with
/// <code>"join": {"table": "&lt;id&gt;", "as": "c", "column": "customer_id", "join_column": "id", "kind": "left"}</code>
/// where <c>column</c> is the queried table's, <c>join_column</c> the joined table's, and <c>kind</c> is <c>inner</c>
/// (the default) or <c>left</c>. The joined table's columns are then named <c>c.&lt;header&gt;</c>.
/// </para>
/// </summary>
public static class TableRequestJson
{
    private static readonly string[] ReadKeys = ["table", "columns", "where", "order_by", "limit", "offset", "seen_as_of", "join"];
    private static readonly string[] AggregateKeys = ["table", "aggregates", "group_by", "where", "having", "order_by", "limit", "seen_as_of", "join"];
    private static readonly string[] JoinKeys = ["table", "as", "column", "join_column", "kind"];
    private const int MaxJoinAlias = 32;
    private static readonly string[] ChangesKeys = ["table", "since", "until", "columns", "limit", "offset"];
    private static readonly string[] FieldKeys = ["column", "aggregate"];
    private static readonly string[] OrderKeys = ["column", "aggregate", "direction"];
    private static readonly string[] ConditionKeys = ["column", "aggregate", "op", "value", "values"];

    public static TableReadRequest ParseRead(JsonElement json)
    {
        RequireObject(json, "The request", ReadKeys);
        return new TableReadRequest(
            TableId(json),
            Optional(json, "columns") is { } columns ? Strings(columns, "columns") : null,
            Optional(json, "where") is { } where ? Filter(where, 1) : null,
            Optional(json, "order_by") is { } order ? Orders(order) : null,
            Optional(json, "limit") is { } limit ? Int(limit, "limit") : null,
            Optional(json, "offset") is { } offset ? Int(offset, "offset") : null,
            Optional(json, "seen_as_of") is { } seen ? Time(seen, "seen_as_of") : null,
            Optional(json, "join") is { } join ? Join(join) : null);
    }

    public static TableAggregateRequest ParseAggregate(JsonElement json)
    {
        RequireObject(json, "The request", AggregateKeys);
        JsonElement aggregates = Optional(json, "aggregates")
            ?? throw new TableQueryException("An aggregate request needs \"aggregates\", such as [{\"aggregate\": \"count\"}].");
        return new TableAggregateRequest(
            TableId(json),
            Array(aggregates, "aggregates").Select(a =>
            {
                RequireObject(a, "Each aggregate", FieldKeys);
                return Field(a);
            }).ToList(),
            Optional(json, "group_by") is { } groupBy ? Strings(groupBy, "group_by") : null,
            Optional(json, "where") is { } where ? Filter(where, 1) : null,
            Optional(json, "having") is { } having ? Filter(having, 1) : null,
            Optional(json, "order_by") is { } order ? Orders(order) : null,
            Optional(json, "limit") is { } limit ? Int(limit, "limit") : null,
            Optional(json, "seen_as_of") is { } seen ? Time(seen, "seen_as_of") : null,
            Optional(json, "join") is { } join ? Join(join) : null);
    }

    public static TableChangesRequest ParseChanges(JsonElement json)
    {
        RequireObject(json, "The request", ChangesKeys);
        JsonElement since = Optional(json, "since")
            ?? throw new TableQueryException("A changes request needs \"since\", a time such as \"2026-11-01T00:00:00Z\".");
        return new TableChangesRequest(
            TableId(json),
            Time(since, "since"),
            Optional(json, "until") is { } until ? Time(until, "until") : null,
            Optional(json, "columns") is { } columns ? Strings(columns, "columns") : null,
            Optional(json, "limit") is { } limit ? Int(limit, "limit") : null,
            Optional(json, "offset") is { } offset ? Int(offset, "offset") : null);
    }

    /// <summary>
    /// True when <paramref name="alias"/> can name a joined table: a letter, then letters, digits or <c>_</c>, at most
    /// 32 characters. It never reaches the SQL; it only prefixes the joined table's headers in a request.
    /// </summary>
    public static bool IsJoinAlias(string alias) =>
        alias.Length is >= 1 and <= MaxJoinAlias
        && char.IsAsciiLetter(alias[0])
        && alias.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static TableJoin Join(JsonElement json)
    {
        RequireObject(json, "\"join\"", JoinKeys);
        Guid table = Optional(json, "table") is { ValueKind: JsonValueKind.String } t && Guid.TryParse(t.GetString(), out Guid id)
            ? id
            : throw new TableQueryException("\"join\" needs \"table\", the id of the table to join.");
        string alias = Optional(json, "as") is { ValueKind: JsonValueKind.String } a && IsJoinAlias(a.GetString()!)
            ? a.GetString()!
            : throw new TableQueryException(
                "\"join\" needs \"as\": a short name for the joined table, starting with a letter, with only letters, digits and _ (at most 32 characters), such as \"c\".");
        string column = JoinColumn(json, "column", "the queried table's column to match");
        string joinColumn = JoinColumn(json, "join_column", "the joined table's column it must equal");
        TableJoinKind kind = Optional(json, "kind") switch
        {
            null => TableJoinKind.Inner,
            { ValueKind: JsonValueKind.String } k when k.GetString() == "inner" => TableJoinKind.Inner,
            { ValueKind: JsonValueKind.String } k when k.GetString() == "left" => TableJoinKind.Left,
            _ => throw new TableQueryException("\"kind\" must be \"inner\" or \"left\"."),
        };
        return new TableJoin(table, alias, column, joinColumn, kind);
    }

    private static string JoinColumn(JsonElement json, string name, string what) =>
        Optional(json, name) is { ValueKind: JsonValueKind.String } c && c.GetString() is { Length: > 0 } header
            ? header
            : throw new TableQueryException($"\"join\" needs \"{name}\", {what}.");

    private static Guid TableId(JsonElement json) =>
        Optional(json, "table") is { ValueKind: JsonValueKind.String } table && Guid.TryParse(table.GetString(), out Guid id)
            ? id
            : throw new TableQueryException("\"table\" must be a table's id.");

    private static TableFilter Filter(JsonElement json, int depth)
    {
        if (depth > TableQueryCompiler.MaxFilterDepth)
            throw new TableQueryException($"A filter can nest at most {TableQueryCompiler.MaxFilterDepth} levels deep.");
        if (json.ValueKind != JsonValueKind.Object)
            throw new TableQueryException("A filter must be an object.");

        if (json.TryGetProperty("and", out JsonElement and))
        {
            RequireObject(json, "An \"and\" filter", ["and"]);
            return new TableAndFilter(Array(and, "and").Select(f => Filter(f, depth + 1)).ToList());
        }
        if (json.TryGetProperty("or", out JsonElement or))
        {
            RequireObject(json, "An \"or\" filter", ["or"]);
            return new TableOrFilter(Array(or, "or").Select(f => Filter(f, depth + 1)).ToList());
        }
        if (json.TryGetProperty("not", out JsonElement not))
        {
            RequireObject(json, "A \"not\" filter", ["not"]);
            return new TableNotFilter(Filter(not, depth + 1));
        }

        RequireObject(json, "A condition", ConditionKeys);
        TableFilterOperator op = Optional(json, "op") is { ValueKind: JsonValueKind.String } o
            ? Operator(o.GetString()!)
            : throw new TableQueryException("A condition needs an \"op\": =, !=, <, <=, >, >=, in, between, is_null, contains or starts_with.");

        JsonElement? value = Optional(json, "value");
        JsonElement? values = Optional(json, "values");
        if (value is not null && values is not null)
            throw new TableQueryException("A condition takes \"value\" or \"values\", not both.");

        IReadOnlyList<object?> list = values is { } many
            ? Array(many, "values").Select(v => (object?)v.Clone()).ToList()
            : value is { } one ? [one.Clone()] : [];
        return new TableCondition(Field(json), op, list);
    }

    private static TableField Field(JsonElement json)
    {
        string? column = Optional(json, "column") switch
        {
            null => null,
            { ValueKind: JsonValueKind.String } c => c.GetString(),
            _ => throw new TableQueryException("\"column\" must be a column's header."),
        };
        TableAggregateFunction? aggregate = Optional(json, "aggregate") switch
        {
            null => null,
            { ValueKind: JsonValueKind.String } a => Aggregate(a.GetString()!),
            _ => throw new TableQueryException("\"aggregate\" must be count, count_distinct, sum, avg, min or max."),
        };
        if (column is null && aggregate is null)
            throw new TableQueryException("Each entry needs a \"column\", an \"aggregate\", or both.");
        return new TableField(column, aggregate);
    }

    private static List<TableOrder> Orders(JsonElement json) =>
        Array(json, "order_by").Select(o =>
        {
            RequireObject(o, "Each order_by entry", OrderKeys);
            TableSortDirection direction = Optional(o, "direction") switch
            {
                null => TableSortDirection.Ascending,
                { ValueKind: JsonValueKind.String } d when d.GetString() == "asc" => TableSortDirection.Ascending,
                { ValueKind: JsonValueKind.String } d when d.GetString() == "desc" => TableSortDirection.Descending,
                _ => throw new TableQueryException("\"direction\" must be \"asc\" or \"desc\"."),
            };
            return new TableOrder(Field(o), direction);
        }).ToList();

    private static TableFilterOperator Operator(string op) => op switch
    {
        "=" => TableFilterOperator.Equal,
        "!=" => TableFilterOperator.NotEqual,
        "<" => TableFilterOperator.Less,
        "<=" => TableFilterOperator.LessOrEqual,
        ">" => TableFilterOperator.Greater,
        ">=" => TableFilterOperator.GreaterOrEqual,
        "in" => TableFilterOperator.In,
        "between" => TableFilterOperator.Between,
        "is_null" => TableFilterOperator.IsNull,
        "contains" => TableFilterOperator.Contains,
        "starts_with" => TableFilterOperator.StartsWith,
        _ => throw new TableQueryException(
            $"Unknown op \"{Clip(op)}\". Use =, !=, <, <=, >, >=, in, between, is_null, contains or starts_with."),
    };

    private static TableAggregateFunction Aggregate(string name) => name switch
    {
        "count" => TableAggregateFunction.Count,
        "count_distinct" => TableAggregateFunction.CountDistinct,
        "sum" => TableAggregateFunction.Sum,
        "avg" => TableAggregateFunction.Avg,
        "min" => TableAggregateFunction.Min,
        "max" => TableAggregateFunction.Max,
        _ => throw new TableQueryException(
            $"Unknown aggregate \"{Clip(name)}\". Use count, count_distinct, sum, avg, min or max."),
    };

    private static List<string> Strings(JsonElement json, string name) =>
        Array(json, name).Select(s => s.ValueKind == JsonValueKind.String
            ? s.GetString()!
            : throw new TableQueryException($"\"{name}\" must be a list of column headers.")).ToList();

    private static int Int(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out int value)
            ? value
            : throw new TableQueryException($"\"{name}\" must be a whole number.");

    private static DateTimeOffset Time(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(json.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out DateTimeOffset value)
            ? value
            : throw new TableQueryException($"\"{name}\" must be an ISO 8601 time, such as \"2026-11-01T09:00:00Z\".");

    private static IEnumerable<JsonElement> Array(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Array
            ? json.EnumerateArray()
            : throw new TableQueryException($"\"{name}\" must be a list.");

    private static JsonElement? Optional(JsonElement json, string name) =>
        json.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value : null;

    private static void RequireObject(JsonElement json, string what, string[] allowed)
    {
        if (json.ValueKind != JsonValueKind.Object)
            throw new TableQueryException($"{what} must be a JSON object.");
        foreach (JsonProperty property in json.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new TableQueryException(
                    $"{what} has an unknown key \"{Clip(property.Name)}\". Allowed keys: {string.Join(", ", allowed)}.");
        }
    }

    private static string Clip(string text) => text.Length > 40 ? text[..40] + "…" : text;
}
