namespace Connapse.Core.Interfaces;

/// <summary>
/// Runs structured queries over a tables container's tables (CON-E6). Callers never send SQL: a request is checked
/// against the table's catalog entry, compiled to one parameterized SELECT, and run read-only on a database login
/// that can only read table data, with time and memory limits and a capped answer. Throws
/// <see cref="TableQueryException"/> for a request it won't run, with a message saying what to fix.
/// </summary>
public interface ITableQueryService
{
    Task<TableQueryResult> ReadAsync(Guid containerId, TableReadRequest request, CancellationToken ct = default);

    Task<TableQueryResult> AggregateAsync(Guid containerId, TableAggregateRequest request, CancellationToken ct = default);
}
