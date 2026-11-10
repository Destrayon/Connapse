using System.Diagnostics;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Connapse.Storage.Tables;

/// <summary>
/// Runs agents' structured table requests (CON-303). The table is looked up in the catalog by container and id, so any
/// container that has tables can be queried whatever its kind. A table whose source document isn't in the request's
/// <see cref="TableAccess"/> is reported exactly as a missing table, and is checked before the request is compiled, so
/// not even a compile error can name its columns. The request is then compiled to one parameterized <c>SELECT</c> and
/// run by <see cref="TableQueryRunner"/>, which applies the limits and caps the answer.
/// </summary>
public sealed class TableQueryService(
    ITableStore tables,
    TableReaderLogin reader,
    TableQueryRunner runner,
    ILogger<TableQueryService> logger) : ITableQueryService
{
    public Task<TableQueryResult> ReadAsync(Guid containerId, TableAccess access, TableReadRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(containerId, access, request.TableId, table => TableQueryCompiler.CompileRead(table, request), ct);
    }

    public Task<TableQueryResult> AggregateAsync(Guid containerId, TableAccess access, TableAggregateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(containerId, access, request.TableId, table => TableQueryCompiler.CompileAggregate(table, request), ct);
    }

    public Task<TableQueryResult> ChangesAsync(Guid containerId, TableAccess access, TableChangesRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(containerId, access, request.TableId, table => TableQueryCompiler.CompileChanges(table, request), ct);
    }

    private async Task<TableQueryResult> RunAsync(
        Guid containerId, TableAccess access, Guid tableId, Func<TableInfo, CompiledTableQuery> compile, CancellationToken ct)
    {
        bool retried = false;
        while (true)
        {
            TableInfo? table = await tables.GetAsync(containerId, tableId, ct);
            if (table is null || !access.CanRead(table))
                throw new TableQueryException($"There is no table with id {tableId} in this container.");
            CompiledTableQuery query = compile(table);
            await reader.EnsureGrantsAsync(containerId, table.StorageSchema, ct);

            Stopwatch elapsed = Stopwatch.StartNew();
            try
            {
                TableQueryResult result = await runner.RunAsync(query, ct);
                logger.LogDebug("Table query on {TableId} returned {Rows} rows in {Elapsed} ms", tableId, result.Rows.Count, elapsed.ElapsedMilliseconds);
                return result;
            }
            catch (PostgresException ex) when (!retried && ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InsufficientPrivilege)
            {
                // The table was reloaded (new storage name) or its schema recreated (grants gone) since we looked.
                retried = true;
                if (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
                    reader.ForgetGrants(table.StorageSchema);
            }
            catch (PostgresException ex)
            {
                logger.LogInformation("Table query on {TableId} failed with {SqlState} after {Elapsed} ms", tableId, ex.SqlState, elapsed.ElapsedMilliseconds);
                throw TableQueryRunner.Describe(ex);
            }
        }
    }
}
