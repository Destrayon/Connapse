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

    [Failure(400, "invalid-page", "Invalid page")]
    InvalidPage,

    [Failure(409, "memory-container", "Agent memory container")]
    MemoryContainer,

    [Failure(403, "agent-sql-off", "Agent SQL is off")]
    AgentSqlOff,

    [Failure(400, "query-not-run", "Query not run")]
    QueryNotRun,
}

/// <summary>One page of a container's tables, by name, with how many it holds in all and whether another page follows.</summary>
public sealed record TableListing(IReadOnlyList<TableInfo> Items, int TotalCount, bool HasMore);

/// <summary>
/// Lists a container's tables a page at a time (CON-319): a documents container holds any number of them beside its
/// files. Open to viewers and reading agent keys. Tables come only from files uploaded to the container, which need no
/// per-document check, so being able to read the container is being able to see its tables.
/// </summary>
[Operation("tables.list", Scope = "knowledge:read", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class ListTables(IContainerStore containers, ITableStore tables, IOperationAuthorizer authorizer)
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

        TablePage page = await tables.ListAsync(containerId, skip, take, ct);
        return Attempt<TableListing, TableStatus>.Succeed(new TableListing(page.Items, page.Total, skip + page.Items.Count < page.Total));
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

        TableAccess readable = await TableRules.AccessAsync(tables, documents, access, context, containerId, ct);
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

/// <summary>Rules the table operations share.</summary>
public static class TableRules
{
    /// <summary>
    /// The source documents of <paramref name="containerId"/>'s tables that the caller may read, checked together by
    /// <see cref="IDocumentAccess.ReadableAsync"/>. A table whose document is gone, belongs to another container or
    /// can't be read is left out, so its rows stay hidden.
    /// </summary>
    public static async Task<TableAccess> AccessAsync(
        ITableStore tables, IDocumentStore documents, IDocumentAccess access, OperationContext context, Guid containerId, CancellationToken ct)
    {
        string owner = containerId.ToString();
        List<Document> sources = [];
        foreach (Guid id in (await tables.ListAsync(containerId, ct)).Select(t => t.DocumentId).OfType<Guid>().Distinct())
        {
            if (await documents.GetAsync(id.ToString(), ct) is { } document
                && string.Equals(document.ContainerId, owner, StringComparison.OrdinalIgnoreCase))
                sources.Add(document);
        }

        IReadOnlySet<string> readable = sources.Count == 0 ? new HashSet<string>() : await access.ReadableAsync(context.User, sources, ct);
        HashSet<Guid> ids = [];
        foreach (string id in readable)
        {
            if (Guid.TryParse(id, out Guid parsed))
                ids.Add(parsed);
        }
        return new TableAccess(ids);
    }
}
