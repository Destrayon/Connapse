using Connapse.Application.Containers;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using IDocumentAccess = Connapse.Application.Ports.IDocumentAccess;

namespace Connapse.Application.Tables;

/// <summary>
/// What a caller may know about one table: its description (visible columns, readable row count, sample rows and
/// rejects), the column that identifies a row across syncs when it's visible, and when its history starts.
/// </summary>
public sealed record TableDetails(TableDescription Description, string? Key, DateTime? HistoryFrom);

/// <summary>
/// The checks the table query operations share (CON-305): a reader, a documents container that exists, and tables the
/// caller may read (CON-304). A table that doesn't exist and one the caller can't read fail the same way, so an answer
/// never shows that an unreadable table is there.
/// </summary>
internal sealed class TableQueryGate(
    IContainerStore containers, ITableStore tables, IDocumentStore documents, IDocumentAccess access, IOperationAuthorizer authorizer,
    IMemoryService memory)
{
    public Task<Attempt<T, TableStatus>> RunAsync<T>(
        OperationContext context, Guid containerId, IReadOnlyList<Guid> tableIds,
        Func<IReadOnlyList<TableInfo>, TableAccess, Task<T>> run, CancellationToken ct) =>
        AttemptAsync(context, containerId, tableIds,
            async (found, readable) => Attempt<T, TableStatus>.Succeed(await run(found, readable)), ct);

    /// <summary>As <see cref="RunAsync{T}"/>, for a run that may itself refuse once the tables are known readable.</summary>
    public async Task<Attempt<T, TableStatus>> AttemptAsync<T>(
        OperationContext context, Guid containerId, IReadOnlyList<Guid> tableIds,
        Func<IReadOnlyList<TableInfo>, TableAccess, Task<Attempt<T, TableStatus>>> run, CancellationToken ct)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Reader))
            return Attempt<T, TableStatus>.Fail(TableStatus.Forbidden, "You don't have permission to read tables.");
        if (await containers.GetAsync(containerId, ct) is not { } container)
            return Attempt<T, TableStatus>.Fail(TableStatus.ContainerNotFound, $"Container {containerId} not found");
        List<TableInfo> found = [];
        TableAccess readable;
        if (container.Kind == ContainerKind.Memory)
        {
            // A memory container's only table is its memory (CON-309), read by memory's own rules.
            foreach (Guid id in tableIds)
            {
                if (id != MemoryTable.Id)
                    return NotFound<T>(id);
            }
            Attempt<TableAccess, TableStatus> memoryAccess =
                await TableRules.MemoryAccessAsync(containers, memory, authorizer, context.User, containerId, ct);
            if (!memoryAccess.Succeeded)
                return Attempt<T, TableStatus>.Fail(memoryAccess.Status, memoryAccess.Detail!);
            readable = memoryAccess.Result!;
            found.AddRange(tableIds.Select(_ => MemoryTable.For(containerId)));
        }
        else
        {
            foreach (Guid id in tableIds)
            {
                if (await tables.GetAsync(containerId, id, ct) is not { } table)
                    return NotFound<T>(id);
                found.Add(table);
            }
            readable = await TableRules.AccessAsync(documents, access, context.User, containerId, found, ct);
        }

        if (!readable.Checked)
            return Attempt<T, TableStatus>.Fail(TableStatus.AccessUnchecked, TableAccess.UncheckedMessage);
        if (found.FirstOrDefault(t => !readable.CanRead(t)) is { } unreadable)
            return NotFound<T>(unreadable.Id);

        try
        {
            return await run(found, readable);
        }
        catch (TableQueryException ex)
        {
            return Attempt<T, TableStatus>.Fail(TableStatus.InvalidRequest, ex.Message);
        }
    }

    private static Attempt<T, TableStatus> NotFound<T>(Guid tableId) =>
        Attempt<T, TableStatus>.Fail(TableStatus.TableNotFound,
            $"There is no table with id {tableId} in this container. List the container's tables to find their ids.");
}

/// <summary>
/// Describes one table (CON-305): its visible columns with their types, how many rows the caller may read, a few sample
/// rows, the rows that couldn't be loaded, the key column and when its history starts. Everything is worked out from
/// the rows the caller may read; hidden columns are left out, and a hidden key reads as none.
/// </summary>
[Operation("tables.describe", Scope = "knowledge:read", Risk = RiskTier.Read)]
public sealed class DescribeTable(
    IContainerStore containers,
    ITableStore tables,
    ITableQueryService queries,
    IDocumentStore documents,
    IDocumentAccess access,
    IOperationAuthorizer authorizer,
    IMemoryService memory)
{
    public Task<Attempt<TableDetails, TableStatus>> RunAsync(
        OperationContext context, Guid containerId, Guid tableId, CancellationToken ct = default) =>
        new TableQueryGate(containers, tables, documents, access, authorizer, memory).RunAsync(context, containerId, [tableId],
            async (found, readable) =>
            {
                TableInfo table = found[0];
                TableDescription description = await queries.DescribeAsync(containerId, readable, tableId, ct);
                return new TableDetails(
                    description,
                    table.Key is { } key && !table.Hides(key) ? key : null,
                    table.HasHistory ? table.HistoryFrom : null);
            }, ct);
}

/// <summary>
/// Reads rows of one table (CON-305): chosen columns, a filter, an order and a page, optionally joined to one other
/// table of the container. Only rows from documents the caller may read come back, hidden columns don't exist, and a
/// request Connapse won't run is refused with what to fix.
/// </summary>
[Operation("tables.read", Scope = "knowledge:read", Risk = RiskTier.Read)]
public sealed class ReadTable(
    IContainerStore containers,
    ITableStore tables,
    ITableQueryService queries,
    IDocumentStore documents,
    IDocumentAccess access,
    IOperationAuthorizer authorizer,
    IMemoryService memory)
{
    public Task<Attempt<TableQueryResult, TableStatus>> RunAsync(
        OperationContext context, Guid containerId, TableReadRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new TableQueryGate(containers, tables, documents, access, authorizer, memory).RunAsync(context, containerId,
            TableIds(request.TableId, request.Join),
            (_, readable) => queries.ReadAsync(containerId, readable, request, ct), ct);
    }

    internal static IReadOnlyList<Guid> TableIds(Guid tableId, TableJoin? join) =>
        join is null || join.TableId == tableId ? [tableId] : [tableId, join.TableId];
}

/// <summary>
/// Counts, sums, averages and finds the least and greatest values of one table (CON-305), over all its readable rows
/// or in groups, optionally joined to one other table of the container. Only rows from documents the caller may read
/// are counted, and hidden columns don't exist.
/// </summary>
[Operation("tables.aggregate", Scope = "knowledge:read", Risk = RiskTier.Read)]
public sealed class AggregateTable(
    IContainerStore containers,
    ITableStore tables,
    ITableQueryService queries,
    IDocumentStore documents,
    IDocumentAccess access,
    IOperationAuthorizer authorizer,
    IMemoryService memory)
{
    public Task<Attempt<TableQueryResult, TableStatus>> RunAsync(
        OperationContext context, Guid containerId, TableAggregateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new TableQueryGate(containers, tables, documents, access, authorizer, memory).RunAsync(context, containerId,
            ReadTable.TableIds(request.TableId, request.Join),
            (_, readable) => queries.AggregateAsync(containerId, readable, request, ct), ct);
    }
}

/// <summary>
/// What changed in one table with history between two times (CON-305 over CON-306): each added, removed or changed
/// key with its old and new row, as Connapse saw them. Only rows from documents the caller may read are compared, and
/// hidden columns don't exist. A table without a key column keeps no history, which is refused only once the caller
/// may read the table, so the refusal never shows that an unreadable table is there.
/// </summary>
[Operation("tables.changes", Scope = "knowledge:read", Risk = RiskTier.Read)]
public sealed class ChangesTable(
    IContainerStore containers,
    ITableStore tables,
    ITableQueryService queries,
    IDocumentStore documents,
    IDocumentAccess access,
    IOperationAuthorizer authorizer,
    IMemoryService memory)
{
    public Task<Attempt<TableQueryResult, TableStatus>> RunAsync(
        OperationContext context, Guid containerId, TableChangesRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new TableQueryGate(containers, tables, documents, access, authorizer, memory).AttemptAsync(context, containerId,
            [request.TableId],
            async (found, readable) => found[0].HasHistory || MemoryTable.Is(found[0])
                ? Attempt<TableQueryResult, TableStatus>.Succeed(await queries.ChangesAsync(containerId, readable, request, ct))
                : Attempt<TableQueryResult, TableStatus>.Fail(TableStatus.NoHistory, NoHistory(found[0])), ct);
    }

    internal static string NoHistory(TableInfo table) =>
        $"Table \"{table.Name}\" keeps no history: it has no key column, so each sync replaces its rows. "
        + "There is nothing to compare, so ask for its rows as they are now instead.";
}
