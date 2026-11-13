using System.Security.Claims;
using Connapse.Application.Containers;
using Connapse.Application.Memory;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using IDocumentAccess = Connapse.Application.Ports.IDocumentAccess;

namespace Connapse.Application.Tables;

/// <summary>Why a table action didn't succeed.</summary>
public enum TableStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(404, "container-not-found", "Container not found")]
    ContainerNotFound,

    [Failure(404, "table-not-found", "Table not found")]
    TableNotFound,

    [Failure(400, "invalid-page", "Invalid page")]
    InvalidPage,

    [Failure(400, "invalid-column", "Invalid column")]
    InvalidColumn,

    [Failure(409, "memory-container", "Agent memory container")]
    MemoryContainer,

    [Failure(503, "tables-access-unchecked", "Access could not be checked")]
    AccessUnchecked,

    [Failure(400, "invalid-table-request", "Invalid table request")]
    InvalidRequest,

    [Failure(409, "table-has-no-history", "Table keeps no history")]
    NoHistory,
}

/// <summary>How a table listing chose and ordered its tables.</summary>
public enum TableListMode
{
    /// <summary>No search: every readable table, by name.</summary>
    All,

    /// <summary>A search in a container with few tables: those whose name, description or a column header contains it.</summary>
    Filtered,

    /// <summary>A search in a container with many tables (CON-310): every readable table, best match first.</summary>
    Ranked,
}

/// <summary>
/// One page of a container's tables with how many it holds in all and whether another page follows. A
/// <see cref="TableListMode.Ranked"/> listing says whether the tables' meaning was compared with the search
/// (<paramref name="MeaningUsed"/>, false when embeddings were unavailable so only words counted) and how many tables
/// weren't compared that way yet (<paramref name="Unembedded"/>).
/// </summary>
public sealed record TableListing(
    IReadOnlyList<TableInfo> Items, int TotalCount, bool HasMore,
    TableListMode Mode = TableListMode.All, bool? MeaningUsed = null, int Unembedded = 0);

/// <summary>What a caller may see of a container's tables (CON-304), shared by the table operations.</summary>
public static class TableRules
{
    /// <summary>
    /// The documents behind <paramref name="tables"/> the caller may read, checked together. A document that's gone or
    /// sits in another container is unreadable. When the check itself fails the access is
    /// <see cref="TableAccess.Unchecked"/>, which reads nothing.
    /// </summary>
    public static async Task<TableAccess> AccessAsync(
        IDocumentStore documents, IDocumentAccess access, ClaimsPrincipal user, Guid containerId,
        IEnumerable<TableInfo> tables, CancellationToken ct)
    {
        try
        {
            var found = new List<Document>();
            foreach (Guid id in tables.SelectMany(Sources).Distinct())
            {
                if (await documents.GetAsync(id.ToString(), ct) is { } document && document.ContainerId == containerId.ToString())
                    found.Add(document);
            }

            IReadOnlySet<string> readable = found.Count == 0 ? new HashSet<string>() : await access.ReadableAsync(user, found, ct);
            return new TableAccess(found.Where(d => readable.Contains(d.Id)).Select(d => Guid.Parse(d.Id)).ToHashSet());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return TableAccess.Unchecked;
        }
    }

    /// <summary>
    /// The copy of <paramref name="table"/> a caller is shown: hidden columns left out (an admin still sees them, marked,
    /// to manage them), rejects trimmed, and only readable source documents named. A merged table the caller reads only
    /// part of is <see cref="TableInfo.Partial"/> and counts <paramref name="readableRows"/>.
    /// </summary>
    public static TableInfo Trim(TableInfo table, TableAccess access, bool admin, long? readableRows) => table with
    {
        Columns = admin ? table.Columns : table.VisibleColumns,
        HiddenColumns = admin ? table.HiddenColumns : null,
        Rejects = access.VisibleRejects(table),
        SourceDocuments = table.IsMerged ? access.ReadableSources(table) : null,
        RowCount = readableRows ?? table.RowCount,
        Partial = readableRows is not null,
    };

    /// <summary>
    /// The access for reading a memory container's <see cref="MemoryTable"/> (CON-309), by memory's own rules: the
    /// container must be one the caller may use as memory (a personal space only by its owner), and the caller sees
    /// only facts whose sources it can all read, team facts or its own. Fails as a missing container or an unchecked
    /// access, never with an access that could read more.
    /// </summary>
    public static async Task<Attempt<TableAccess, TableStatus>> MemoryAccessAsync(
        IContainerStore containers, IMemoryService memory, IOperationAuthorizer authorizer, ClaimsPrincipal user, Guid containerId,
        CancellationToken ct)
    {
        if (!await authorizer.AllowsAsync(user, Policies.KnowledgeRead))
            return Attempt<TableAccess, TableStatus>.Fail(TableStatus.Forbidden, "Reading memory needs knowledge:read.");
        MemoryCaller caller = MemoryRules.Caller(user);
        (Guid? found, _) = await MemoryRules.ContainerAsync(containers, containerId.ToString(), caller, ct);
        if (found != containerId)
            return Attempt<TableAccess, TableStatus>.Fail(TableStatus.ContainerNotFound, $"Container {containerId} not found");
        return await memory.ViewerOfAsync(containerId, caller, ct) is { } viewer
            ? Attempt<TableAccess, TableStatus>.Succeed(new TableAccess(new HashSet<Guid>()) { Memory = viewer })
            : Attempt<TableAccess, TableStatus>.Fail(TableStatus.AccessUnchecked, TableAccess.UncheckedMessage);
    }

    private static IEnumerable<Guid> Sources(TableInfo table) =>
        table.DocumentId is Guid document ? [document] : table.SourceDocuments ?? [];
}

/// <summary>
/// Lists a container's tables a page at a time (CON-319): a documents container holds any number of them beside its
/// files. Open to viewers and reading agent keys. Only tables built from documents the caller may read are listed,
/// each trimmed to what the caller may see (CON-304); when that can't be checked, nothing is. A search keeps the tables
/// whose name, description or a visible column's header contains it, ignoring case (CON-305). Over
/// <see cref="TableSearch.ManyTables"/> readable tables a search instead ranks every one of them by its card's words
/// and meaning (CON-310); hidden columns are never part of a card, so they can't move a table up.
/// </summary>
[Operation("tables.list", Scope = "knowledge:read", Risk = RiskTier.Read)]
public sealed class ListTables(
    IContainerStore containers,
    ITableStore tables,
    ITableQueryService queries,
    IDocumentStore documents,
    IDocumentAccess access,
    IOperationAuthorizer authorizer,
    IMemoryService memory,
    ITableCardIndex cards)
{
    public const int MaxTake = 200;

    public async Task<Attempt<TableListing, TableStatus>> RunAsync(
        OperationContext context, Guid containerId, int skip, int take, string? search = null, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Reader))
            return Attempt<TableListing, TableStatus>.Fail(TableStatus.Forbidden, "You don't have permission to view tables.");
        if (skip < 0)
            return Attempt<TableListing, TableStatus>.Fail(TableStatus.InvalidPage, "skip must be >= 0");
        if (take < 1)
            return Attempt<TableListing, TableStatus>.Fail(TableStatus.InvalidPage, "take must be >= 1");
        if (take > MaxTake)
            return Attempt<TableListing, TableStatus>.Fail(TableStatus.InvalidPage, $"take must be <= {MaxTake}");
        if (await containers.GetAsync(containerId, ct) is not { } container)
            return Attempt<TableListing, TableStatus>.Fail(TableStatus.ContainerNotFound, $"Container {containerId} not found");
        if (container.Kind == ContainerKind.Memory)
            return await MemoryAsync(context, containerId, skip, take, search, ct);

        IReadOnlyList<TableInfo> all = await tables.ListAsync(containerId, ct);
        TableAccess readable = await TableRules.AccessAsync(documents, access, context.User, containerId, all, ct);
        if (!readable.Checked)
            return Attempt<TableListing, TableStatus>.Fail(TableStatus.AccessUnchecked, TableAccess.UncheckedMessage);

        string? term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        bool admin = await authorizer.AllowsAsync(context.User, Policies.Admin);
        // An admin is also shown the tables hidden from everyone (CON-312), marked, to show them again.
        List<TableInfo> visible = all
            .Where(t => readable.CanRead(t) || admin && t.Hidden && readable.CanRead(t with { Hidden = false }))
            .ToList();
        TableListMode mode = term is null ? TableListMode.All
            : visible.Count > TableSearch.ManyTables ? TableListMode.Ranked
            : TableListMode.Filtered;
        bool? meaningUsed = null;
        int unembedded = 0;
        if (mode == TableListMode.Filtered)
            visible = visible.Where(t => Matches(t, term!)).ToList();
        else if (mode == TableListMode.Ranked)
        {
            TableCard[] tableCards = [.. visible.Select(TableCard.For)];
            TableCardSimilarities found = await SimilaritiesAsync(containerId, tableCards, term!, ct);
            Dictionary<Guid, TableInfo> byId = visible.ToDictionary(t => t.Id);
            visible = TableSearch.Rank(tableCards, term!, found.Scores).Select(id => byId[id]).ToList();
            meaningUsed = found.Available;
            unembedded = found.Unembedded;
        }

        var items = new List<TableInfo>();
        foreach (TableInfo table in visible.Skip(skip).Take(take))
        {
            long? readableRows = null;
            if (!table.Hidden && !readable.SeesAll(table))
            {
                try
                {
                    readableRows = await queries.CountAsync(containerId, readable, table.Id, ct);
                }
                catch (TableQueryException)
                {
                    continue; // Deleted since it was listed.
                }
            }
            items.Add(TableRules.Trim(table, readable, admin, readableRows));
        }

        return Attempt<TableListing, TableStatus>.Succeed(
            new TableListing(items, visible.Count, skip + take < visible.Count, mode, meaningUsed, unembedded));
    }

    /// <summary>The cards' similarity to the search, or none when it can't be had, so the ranking falls back to words.</summary>
    private async Task<TableCardSimilarities> SimilaritiesAsync(
        Guid containerId, IReadOnlyList<TableCard> tableCards, string term, CancellationToken ct)
    {
        try
        {
            return await cards.SimilaritiesAsync(containerId, tableCards, term, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return TableCardSimilarities.Unavailable(tableCards.Count);
        }
    }

    /// <summary>
    /// A memory container holds one table, <see cref="MemoryTable"/> (CON-309), counted over the facts the caller can
    /// see now.
    /// </summary>
    private async Task<Attempt<TableListing, TableStatus>> MemoryAsync(
        OperationContext context, Guid containerId, int skip, int take, string? search, CancellationToken ct)
    {
        Attempt<TableAccess, TableStatus> readable =
            await TableRules.MemoryAccessAsync(containers, memory, authorizer, context.User, containerId, ct);
        if (!readable.Succeeded)
            return Attempt<TableListing, TableStatus>.Fail(readable.Status, readable.Detail!);

        TableInfo table = MemoryTable.For(containerId);
        string? term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        TableListMode mode = term is null ? TableListMode.All : TableListMode.Filtered;
        if (term is not null && !Matches(table, term))
            return Attempt<TableListing, TableStatus>.Succeed(new TableListing([], 0, false, mode));
        if (skip > 0)
            return Attempt<TableListing, TableStatus>.Succeed(new TableListing([], 1, false, mode));

        long rows;
        try
        {
            rows = await queries.CountAsync(containerId, readable.Result!, table.Id, ct);
        }
        catch (TableQueryException ex)
        {
            return Attempt<TableListing, TableStatus>.Fail(TableStatus.InvalidRequest, ex.Message);
        }
        return Attempt<TableListing, TableStatus>.Succeed(new TableListing([table with { RowCount = rows }], 1, false, mode));
    }

    /// <summary>Whether the table's name, description or a visible column's header contains <paramref name="term"/>.</summary>
    private static bool Matches(TableInfo table, string term) =>
        table.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
        || table.Description?.Contains(term, StringComparison.OrdinalIgnoreCase) == true
        || table.VisibleColumns.Any(c => c.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Hides a table's columns from every caller (CON-304), replacing the ones hidden before; an empty list shows them
/// all. A hidden column can't be selected, filtered, grouped, ordered or joined on, and isn't listed or described.
/// Names are the columns' headers, and stay hidden across reloads. Admins only.
/// </summary>
[Operation("tables.hideColumns", Scope = "admin:settings", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class HideTableColumns(IContainerStore containers, ITableStore tables, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<TableInfo, TableStatus>> RunAsync(
        OperationContext context, Guid containerId, Guid tableId, IReadOnlyList<string> columns, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (!await authorizer.AllowsAsync(context.User, Policies.Admin))
            return Attempt<TableInfo, TableStatus>.Fail(TableStatus.Forbidden, "Only admins can hide table columns.");
        if (await containers.GetAsync(containerId, ct) is not { } container)
            return Attempt<TableInfo, TableStatus>.Fail(TableStatus.ContainerNotFound, $"Container {containerId} not found");
        if (container.Kind == ContainerKind.Memory)
            return Attempt<TableInfo, TableStatus>.Fail(TableStatus.MemoryContainer, ContainerRules.MemoryOnly(container));

        try
        {
            return await tables.SetHiddenColumnsAsync(containerId, tableId, columns, ct) is { } table
                ? Attempt<TableInfo, TableStatus>.Succeed(table)
                : Attempt<TableInfo, TableStatus>.Fail(TableStatus.TableNotFound, $"Table {tableId} not found");
        }
        catch (ArgumentException ex)
        {
            return Attempt<TableInfo, TableStatus>.Fail(TableStatus.InvalidColumn, ex.Message);
        }
    }
}
