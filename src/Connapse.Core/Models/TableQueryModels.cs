namespace Connapse.Core;

/// <summary>A comparison in a table filter (CON-E6). The agent picks one; Connapse writes the SQL for it.</summary>
public enum TableFilterOperator
{
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    In,
    Between,
    IsNull,
    Contains,
    StartsWith,
}

public enum TableAggregateFunction
{
    Count,
    CountDistinct,
    Sum,
    Avg,
    Min,
    Max,
}

public enum TableSortDirection
{
    Ascending,
    Descending,
}

/// <summary>
/// A column of a table by its original header, or an aggregate over one. <see cref="Column"/> is null only for
/// <c>count</c> of all rows.
/// </summary>
public sealed record TableField(string? Column, TableAggregateFunction? Aggregate = null);

/// <summary>A filter: <see cref="TableAndFilter"/>, <see cref="TableOrFilter"/>, <see cref="TableNotFilter"/> or a <see cref="TableCondition"/>.</summary>
public abstract record TableFilter;

public sealed record TableAndFilter(IReadOnlyList<TableFilter> All) : TableFilter;

public sealed record TableOrFilter(IReadOnlyList<TableFilter> Any) : TableFilter;

public sealed record TableNotFilter(TableFilter Inner) : TableFilter;

/// <summary>
/// One comparison. <see cref="Values"/> holds no value for <c>is_null</c>, two for <c>between</c>, one or more for
/// <c>in</c>, and one otherwise. Values may be CLR values or <c>System.Text.Json.JsonElement</c>s; each is converted
/// to its column's type and bound as a parameter, never written into the SQL.
/// </summary>
public sealed record TableCondition(TableField Field, TableFilterOperator Operator, IReadOnlyList<object?> Values) : TableFilter;

public sealed record TableOrder(TableField Field, TableSortDirection Direction = TableSortDirection.Ascending);

/// <summary>Reads rows of one table: chosen columns (all when null), a filter, an order, a limit and an offset.</summary>
public sealed record TableReadRequest(
    Guid TableId,
    IReadOnlyList<string>? Columns = null,
    TableFilter? Where = null,
    IReadOnlyList<TableOrder>? OrderBy = null,
    int? Limit = null,
    int? Offset = null);

/// <summary>Aggregates one table's rows, optionally grouped, with a row filter and a filter on the aggregates.</summary>
public sealed record TableAggregateRequest(
    Guid TableId,
    IReadOnlyList<TableField> Aggregates,
    IReadOnlyList<string>? GroupBy = null,
    TableFilter? Where = null,
    TableFilter? Having = null,
    IReadOnlyList<TableOrder>? OrderBy = null,
    int? Limit = null);

/// <summary>
/// A query's answer. Cells are untrusted data from the table. <see cref="Truncated"/> says the answer was cut at the
/// row or character cap (or a long cell was shortened), and <see cref="TruncatedReason"/> says which.
/// </summary>
public sealed record TableQueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool Truncated,
    string? TruncatedReason);

/// <summary>
/// A request Connapse won't run, with a message saying what to fix (for example, the columns that do exist), so an
/// agent can correct the request and retry.
/// </summary>
public sealed class TableQueryException(string message) : Exception(message);
