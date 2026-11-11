using System.Security.Claims;
using Connapse.Application.Containers;
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

    [Failure(403, "agent-sql-off", "Agent SQL is off")]
    AgentSqlOff,

    [Failure(400, "query-not-run", "Query not run")]
    QueryNotRun,
}

/// <summary>One page of a container's tables, by name, with how many it holds in all and whether another page follows.</summary>
public sealed record TableListing(IReadOnlyList<TableInfo> Items, int TotalCount, bool HasMore);

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

    private static IEnumerable<Guid> Sources(TableInfo table) =>
        table.DocumentId is Guid document ? [document] : table.SourceDocuments ?? [];
}

/// <summary>
/// Lists a container's tables a page at a time (CON-319): a documents container holds any number of them beside its
/// files. Open to viewers and reading agent keys. Only tables built from documents the caller may read are listed,
/// each trimmed to what the caller may see (CON-304); when that can't be checked, nothing is.
/// </summary>
[Operation("tables.list", Scope = "knowledge:read", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class ListTables(
    IContainerStore containers,
    ITableStore tables,
    ITableQueryService queries,
    IDocumentStore documents,
    IDocumentAccess access,
    IOperationAuthorizer authorizer)
{
    public const int MaxTake = 200;

    public async Task<Attempt<TableListing, TableStatus>> RunAsync(
        OperationContext context, Guid containerId, int skip, int take, CancellationToken ct = default)
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
            return Attempt<TableListing, TableStatus>.Fail(TableStatus.MemoryContainer, ContainerRules.MemoryOnly(container));

        IReadOnlyList<TableInfo> all = await tables.ListAsync(containerId, ct);
        TableAccess readable = await TableRules.AccessAsync(documents, access, context.User, containerId, all, ct);
        if (!readable.Checked)
            return Attempt<TableListing, TableStatus>.Fail(TableStatus.AccessUnchecked, TableAccess.UncheckedMessage);

        List<TableInfo> visible = all.Where(readable.CanRead).ToList();
        bool admin = await authorizer.AllowsAsync(context.User, Policies.Admin);
        var items = new List<TableInfo>();
        foreach (TableInfo table in visible.Skip(skip).Take(take))
        {
            long? readableRows = null;
            if (!readable.SeesAll(table))
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

        return Attempt<TableListing, TableStatus>.Succeed(new TableListing(items, visible.Count, skip + take < visible.Count));
    }
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

/// <summary>
/// Runs a read-only query an agent wrote itself over a container's tables (CON-326). Off until an administrator turns
/// on the container's <c>Tables.AgentSql</c> setting. Open to viewers and reading agent keys, but the query sees only
/// the tables whose source document the caller may read, by the same check search runs on each hit; the database's
/// row-level security enforces that set again (docs/research/rls-raw-sql-agents-2026-11-10.md).
/// </summary>
[Operation("tables.sql", Scope = "knowledge:read", Risk = RiskTier.Read)]
public sealed class RunTableSql(
    IContainerStore containers,
    ITableStore tables,
    IDocumentStore documents,
    IDocumentAccess access,
    IContainerSettingsResolver settings,
    ITableSqlService sql,
    IOperationAuthorizer authorizer)
{
    public async Task<Attempt<TableQueryResult, TableStatus>> RunAsync(
        OperationContext context, Guid containerId, string? query, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Reader))
            return Attempt<TableQueryResult, TableStatus>.Fail(TableStatus.Forbidden, "You don't have permission to query tables.");
        if (string.IsNullOrWhiteSpace(query))
            return Attempt<TableQueryResult, TableStatus>.Fail(TableStatus.QueryNotRun, "sql is required", "sql");
        if (await containers.GetAsync(containerId, ContainerRules.Viewer(context), ct) is not { } container)
            return Attempt<TableQueryResult, TableStatus>.Fail(TableStatus.ContainerNotFound, $"Container {containerId} not found");
        if (container.Kind == ContainerKind.Memory)
            return Attempt<TableQueryResult, TableStatus>.Fail(TableStatus.MemoryContainer, ContainerRules.MemoryOnly(container));
        if (!(await settings.GetTableSettingsAsync(containerId, ct)).AgentSql)
            return Attempt<TableQueryResult, TableStatus>.Fail(TableStatus.AgentSqlOff,
                $"Agents can't write their own SQL in '{container.Name}'. An administrator can turn on Tables.AgentSql in its settings.");

        IReadOnlyList<TableInfo> all = await tables.ListAsync(containerId, ct);
        TableAccess readable = await TableRules.AccessAsync(documents, access, context.User, containerId, all, ct);
        if (!readable.Checked)
            return Attempt<TableQueryResult, TableStatus>.Fail(TableStatus.AccessUnchecked, TableAccess.UncheckedMessage);
        try
        {
            return Attempt<TableQueryResult, TableStatus>.Succeed(await sql.RunAsync(containerId, readable, query, ct));
        }
        catch (TableQueryException ex)
        {
            return Attempt<TableQueryResult, TableStatus>.Fail(TableStatus.QueryNotRun, ex.Message, "sql");
        }
    }
}
