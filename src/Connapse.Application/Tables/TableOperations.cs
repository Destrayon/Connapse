using Connapse.Application.Containers;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;

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
