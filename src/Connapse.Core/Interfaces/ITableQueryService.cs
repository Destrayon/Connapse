namespace Connapse.Core.Interfaces;

/// <summary>
/// Runs structured queries over a tables container's tables (CON-E6). Callers never send SQL: a request is checked
/// against the table's catalog entry, compiled to one parameterized SELECT, and run read-only on a database login
/// that can only read table data, with time and memory limits and a capped answer. A table whose source document is
/// not in the request's <see cref="TableAccess"/> reads exactly as a missing one. Throws <see cref="TableQueryException"/> for a
/// request it won't run, with a message saying what to fix.
/// </summary>
public interface ITableQueryService
{
    Task<TableQueryResult> ReadAsync(Guid containerId, TableAccess access, TableReadRequest request, CancellationToken ct = default);

    Task<TableQueryResult> AggregateAsync(Guid containerId, TableAccess access, TableAggregateRequest request, CancellationToken ct = default);

    /// <summary>What changed in a table with history between two times (CON-306); see <see cref="TableChangesRequest"/>.</summary>
    Task<TableQueryResult> ChangesAsync(Guid containerId, TableAccess access, TableChangesRequest request, CancellationToken ct = default);

    /// <summary>
    /// What the caller may know about a table (CON-304): its visible columns, and a row count, a few sample rows and the
    /// rejected rows from only the rows it may read.
    /// </summary>
    Task<TableDescription> DescribeAsync(Guid containerId, TableAccess access, Guid tableId, CancellationToken ct = default);

    /// <summary>How many of a table's rows the caller may read (CON-304).</summary>
    Task<long> CountAsync(Guid containerId, TableAccess access, Guid tableId, CancellationToken ct = default);
}
