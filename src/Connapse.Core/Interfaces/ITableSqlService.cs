namespace Connapse.Core.Interfaces;

/// <summary>
/// Runs a read-only query an agent wrote itself over a container's tables (CON-326). The query may read only the tables
/// whose source document is in the request's <see cref="TableAccess"/>; it is checked by Connapse's SQL allowlist, then
/// run on the reader login under row-level security with the same time, memory and size limits as the structured
/// table tools. Throws <see cref="TableQueryException"/> for a query it won't run or that fails, with a message that
/// names what to fix and never shows rows the caller may not read.
/// </summary>
public interface ITableSqlService
{
    Task<TableQueryResult> RunAsync(Guid containerId, TableAccess access, string sql, CancellationToken ct = default);
}
