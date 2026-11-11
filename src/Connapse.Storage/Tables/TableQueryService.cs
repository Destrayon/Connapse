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
/// <para>
/// Permission trimming (CON-304): an access that couldn't be checked answers nothing; a table merged from several
/// documents shows only the rows of the readable ones; hidden columns read as missing; and
/// <see cref="DescribeAsync"/> works out its count, sample rows and rejects from the readable rows alone.
/// </para>
/// <para>
/// A read or an aggregate can join one other table (CON-305). The joined table is looked up in the same container and
/// checked against the same access before anything is compiled, so a table the caller can't read is missing whether it
/// is queried or joined, and a joined merged table shows only its readable rows.
/// </para>
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
        return RunAsync(containerId, access, request.TableId, request.Join?.TableId,
            (table, joined) => TableQueryCompiler.CompileRead(table, request, access.ReadableSources(table),
                joined, joined is null ? null : access.ReadableSources(joined)), ct);
    }

    public Task<TableQueryResult> AggregateAsync(Guid containerId, TableAccess access, TableAggregateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(containerId, access, request.TableId, request.Join?.TableId,
            (table, joined) => TableQueryCompiler.CompileAggregate(table, request, access.ReadableSources(table),
                joined, joined is null ? null : access.ReadableSources(joined)), ct);
    }

    public async Task<TableDescription> DescribeAsync(Guid containerId, TableAccess access, Guid tableId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        TableInfo? described = null;
        TableQueryResult count = await RunAsync(containerId, access, tableId, table =>
        {
            described = table;
            return TableQueryCompiler.CompileCount(table, access.ReadableSources(table));
        }, ct);
        TableInfo table = described!;
        // The sample must come from the same load as the count; a reload in between gets the new load's sample, which
        // is still trimmed to the caller.
        TableQueryResult sample = await RunAsync(containerId, access, tableId,
            t => TableQueryCompiler.CompileSample(t, access.ReadableSources(t)), ct);

        return new TableDescription(
            table.Id,
            table.Name,
            table.Description,
            table.VisibleColumns,
            Convert.ToInt64(count.Rows[0][0], System.Globalization.CultureInfo.InvariantCulture),
            sample,
            table.NeedsReview,
            access.VisibleRejects(table));
    }

    public async Task<long> CountAsync(Guid containerId, TableAccess access, Guid tableId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        TableQueryResult count = await RunAsync(containerId, access, tableId,
            table => TableQueryCompiler.CompileCount(table, access.ReadableSources(table)), ct);
        return Convert.ToInt64(count.Rows[0][0], System.Globalization.CultureInfo.InvariantCulture);
    }

    public Task<TableQueryResult> ChangesAsync(Guid containerId, TableAccess access, TableChangesRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(containerId, access, request.TableId, table => TableQueryCompiler.CompileChanges(table, request, access.ReadableSources(table)), ct);
    }

    private Task<TableQueryResult> RunAsync(
        Guid containerId, TableAccess access, Guid tableId, Func<TableInfo, CompiledTableQuery> compile, CancellationToken ct) =>
        RunAsync(containerId, access, tableId, joinedId: null, (table, _) => compile(table), ct);

    private async Task<TableQueryResult> RunAsync(
        Guid containerId, TableAccess access, Guid tableId, Guid? joinedId, Func<TableInfo, TableInfo?, CompiledTableQuery> compile, CancellationToken ct)
    {
        if (!access.Checked)
            throw new TableQueryException(TableAccess.UncheckedMessage);

        bool retried = false;
        while (true)
        {
            TableInfo table = await ReadableAsync(containerId, access, tableId, ct);
            TableInfo? joined = joinedId is Guid id ? await ReadableAsync(containerId, access, id, ct) : null;
            CompiledTableQuery query = compile(table, joined);
            await reader.EnsureGrantsAsync(containerId, table.StorageSchema, ct);
            if (joined is not null && joined.StorageSchema != table.StorageSchema)
                await reader.EnsureGrantsAsync(containerId, joined.StorageSchema, ct);

            Stopwatch elapsed = Stopwatch.StartNew();
            try
            {
                TableQueryResult result = await runner.RunAsync(containerId, access, query, ct);
                logger.LogDebug("Table query on {TableId} returned {Rows} rows in {Elapsed} ms", tableId, result.Rows.Count, elapsed.ElapsedMilliseconds);
                return result;
            }
            catch (PostgresException ex) when (!retried && ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InsufficientPrivilege)
            {
                // The table was reloaded (new storage name) or its schema recreated (grants gone) since we looked.
                retried = true;
                if (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
                {
                    reader.ForgetGrants(table.StorageSchema);
                    if (joined is not null)
                        reader.ForgetGrants(joined.StorageSchema);
                }
            }
            catch (PostgresException ex)
            {
                logger.LogInformation("Table query on {TableId} failed with {SqlState} after {Elapsed} ms", tableId, ex.SqlState, elapsed.ElapsedMilliseconds);
                throw TableQueryRunner.Describe(ex);
            }
        }
    }

    /// <summary>The table, or the same missing-table error whether it doesn't exist or the caller can't read it.</summary>
    private async Task<TableInfo> ReadableAsync(Guid containerId, TableAccess access, Guid tableId, CancellationToken ct)
    {
        TableInfo? table = await tables.GetAsync(containerId, tableId, ct);
        return table is not null && access.CanRead(table)
            ? table
            : throw new TableQueryException($"There is no table with id {tableId} in this container.");
    }
}
