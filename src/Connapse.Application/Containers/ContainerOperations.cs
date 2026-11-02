using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Vectors;

namespace Connapse.Application.Containers;

/// <summary>Why a container action didn't succeed.</summary>
public enum ContainerStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(404, "container-not-found", "Container not found")]
    NotFound,

    [Failure(400, "invalid-container", "Invalid container")]
    Invalid,

    [Failure(409, "container-conflict", "Container already exists")]
    Conflict,

    [Failure(400, "container-not-empty", "Container isn't empty")]
    NotEmpty,

    [Failure(400, "invalid-page", "Invalid page")]
    InvalidPage,

    [Failure(400, "invalid-reindex", "Invalid reindex request")]
    InvalidReindex,
}

/// <summary>Names a container by id, or by name as REST's by-name route and MCP's tools accept.</summary>
public sealed record ContainerKey(Guid? Id, string? Name)
{
    public static ContainerKey ById(Guid id) => new(id, null);

    public static ContainerKey ByName(string name) => new(null, name);

    /// <summary>An id when the text is one, a name otherwise: what an MCP tool's containerId argument means.</summary>
    public static ContainerKey Parse(string idOrName) =>
        Guid.TryParse(idOrName, out var id) ? ById(id) : ByName(idOrName);

    /// <summary>How "not found" names it: the id bare, a name in quotes, as REST always has.</summary>
    public override string ToString() => Id is Guid id ? id.ToString() : $"'{Name}'";
}

/// <summary>
/// One page of containers. <see cref="Take"/> null means every container, which only MCP's
/// container_list asks for; REST and the Home page always name a page.
/// </summary>
public sealed record ListContainersQuery(int Skip = 0, int? Take = null);

public sealed record ContainerList(IReadOnlyList<Container> Items, bool HasMore);

/// <summary>A container's counts, chunk total, size and embedding models, as REST, MCP and the file browser show them.</summary>
public sealed record ContainerStatsView(Container Container, ContainerStats Stats, IReadOnlyList<EmbeddingModelInfo> Models);

/// <summary>What a rename or edit asks to change; a null field is left as it is.</summary>
public sealed record ContainerChanges(string? Name = null, string? Description = null);

internal static class ContainerRules
{
    public const string NameRule = "Container name must be 2-128 characters, lowercase alphanumeric and hyphens, cannot start or end with a hyphen";

    public static async Task<Container?> FindAsync(IContainerStore containers, ContainerKey key, CancellationToken ct) =>
        key.Id is Guid id ? await containers.GetAsync(id, ct)
        : string.IsNullOrWhiteSpace(key.Name) ? null
        : await containers.GetByNameAsync(key.Name.Trim().ToLowerInvariant(), ct);

    public static Attempt<T, ContainerStatus> NotFound<T>(ContainerKey key) =>
        Attempt<T, ContainerStatus>.Fail(ContainerStatus.NotFound, $"Container {key} not found");
}

/// <summary>Lists containers. Open to viewers; a container has no per-item visibility.</summary>
[Operation("containers.list", Scope = "knowledge:read", Risk = RiskTier.Read)]
public sealed class ListContainers(IContainerStore containers, IOperationAuthorizer authorizer)
{
    public const int MaxTake = 200;

    public async Task<Attempt<ContainerList, ContainerStatus>> RunAsync(OperationContext context, ListContainersQuery query, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Reader))
            return Attempt<ContainerList, ContainerStatus>.Fail(ContainerStatus.Forbidden, "You don't have permission to view containers.");
        if (query.Skip < 0)
            return Attempt<ContainerList, ContainerStatus>.Fail(ContainerStatus.InvalidPage, "skip must be >= 0");
        if (query.Take is < 1)
            return Attempt<ContainerList, ContainerStatus>.Fail(ContainerStatus.InvalidPage, "take must be >= 1");
        if (query.Take is > MaxTake)
            return Attempt<ContainerList, ContainerStatus>.Fail(ContainerStatus.InvalidPage, $"take must be <= {MaxTake}");

        // One more than the page, to know whether another follows.
        var listed = await containers.ListAsync(query.Skip, query.Take is int take ? take + 1 : int.MaxValue, ct);
        bool hasMore = query.Take is int size && listed.Count > size;
        return Attempt<ContainerList, ContainerStatus>.Succeed(new ContainerList(hasMore ? listed.Take(query.Take!.Value).ToList() : listed, hasMore));
    }
}

/// <summary>Reads one container by id or name.</summary>
[Operation("containers.get", Scope = "knowledge:read", Risk = RiskTier.Read)]
public sealed class GetContainer(IContainerStore containers, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<Container, ContainerStatus>> RunAsync(OperationContext context, ContainerKey key, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Reader))
            return Attempt<Container, ContainerStatus>.Fail(ContainerStatus.Forbidden, "You don't have permission to view containers.");

        return await ContainerRules.FindAsync(containers, key, ct) is { } container
            ? Attempt<Container, ContainerStatus>.Succeed(container)
            : ContainerRules.NotFound<Container>(key);
    }
}

/// <summary>Creates a container. Editors and above; its name must be unique and URL-safe.</summary>
[Operation("containers.create", Scope = "knowledge:write", Risk = RiskTier.Write)]
public sealed class CreateContainer(IContainerStore containers, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<Container, ContainerStatus>> RunAsync(OperationContext context, CreateContainerRequest request, CancellationToken ct = default)
    {
        static Attempt<Container, ContainerStatus> Fail(ContainerStatus status, string detail, string? field = null) =>
            Attempt<Container, ContainerStatus>.Fail(status, detail, field);

        if (!await authorizer.AllowsAsync(context.User, Policies.Writer))
            return Fail(ContainerStatus.Forbidden, "You don't have permission to create containers.");
        string name = request.Name?.Trim() ?? "";
        if (name.Length == 0)
            return Fail(ContainerStatus.Invalid, "Container name is required", "name");
        if (!PathUtilities.IsValidContainerName(name))
            return Fail(ContainerStatus.Invalid, ContainerRules.NameRule, "name");
        if (await containers.GetByNameAsync(name, ct) is not null)
            return Fail(ContainerStatus.Conflict, $"Container '{name}' already exists", "name");

        Container created;
        try
        {
            created = await containers.CreateAsync(new CreateContainerRequest(name, request.Description?.Trim()), ct);
        }
        catch (InvalidOperationException ex)
        {
            // The store's own duplicate check, when another create got there between ours and this.
            return Fail(ContainerStatus.Conflict, ex.Message, "name");
        }

        await audit.LogAsync("container.created", "container", created.Id, new { created.Name }, ct);
        return Attempt<Container, ContainerStatus>.Succeed(created);
    }
}

/// <summary>
/// Renames a container or changes its description. Editors and above. The new name follows the
/// create rules; files are stored by the container's id, so they don't move.
/// </summary>
[Operation("containers.rename", Scope = "knowledge:write", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class RenameContainer(IContainerStore containers, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<Container, ContainerStatus>> RunAsync(OperationContext context, Guid containerId, ContainerChanges changes, CancellationToken ct = default)
    {
        static Attempt<Container, ContainerStatus> Fail(ContainerStatus status, string detail, string? field = null) =>
            Attempt<Container, ContainerStatus>.Fail(status, detail, field);

        if (!await authorizer.AllowsAsync(context.User, Policies.Writer))
            return Fail(ContainerStatus.Forbidden, "You don't have permission to change containers.");
        if (changes.Name is null && changes.Description is null)
            return Fail(ContainerStatus.Invalid, "A new name or description is required");

        string? name = changes.Name?.Trim();
        if (name is not null && !PathUtilities.IsValidContainerName(name))
            return Fail(ContainerStatus.Invalid, name.Length == 0 ? "Container name is required" : ContainerRules.NameRule, "name");

        var current = await containers.GetAsync(containerId, ct);
        if (current is null)
            return ContainerRules.NotFound<Container>(ContainerKey.ById(containerId));
        if (name is not null && name != current.Name && await containers.GetByNameAsync(name, ct) is not null)
            return Fail(ContainerStatus.Conflict, $"Container '{name}' already exists", "name");

        Container? updated;
        try
        {
            updated = await containers.UpdateAsync(containerId, name, changes.Description is null ? null : changes.Description.Trim(), ct);
        }
        catch (InvalidOperationException ex)
        {
            return Fail(ContainerStatus.Conflict, ex.Message, "name");
        }
        if (updated is null)
            return ContainerRules.NotFound<Container>(ContainerKey.ById(containerId));

        await audit.LogAsync("container.updated", "container", updated.Id, new { From = current.Name, updated.Name }, ct);
        return Attempt<Container, ContainerStatus>.Succeed(updated);
    }
}

/// <summary>Deletes an empty container. Editors and above; one with files is refused, since deleting it deletes them.</summary>
[Operation("containers.delete", Scope = "knowledge:write", Risk = RiskTier.Destructive)]
public sealed class DeleteContainer(IContainerStore containers, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<Container, ContainerStatus>> RunAsync(OperationContext context, ContainerKey key, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Writer))
            return Attempt<Container, ContainerStatus>.Fail(ContainerStatus.Forbidden, "You don't have permission to delete containers.");

        var container = await ContainerRules.FindAsync(containers, key, ct);
        if (container is null || !Guid.TryParse(container.Id, out var id))
            return ContainerRules.NotFound<Container>(key);

        try
        {
            if (!await containers.DeleteAsync(id, ct))
                return ContainerRules.NotFound<Container>(key);
        }
        catch (InvalidOperationException ex)
        {
            // The store refuses a container that still has documents, and says how many.
            return Attempt<Container, ContainerStatus>.Fail(ContainerStatus.NotEmpty, ex.Message);
        }

        await audit.LogAsync("container.deleted", "container", container.Id, new { container.Name }, ct);
        return Attempt<Container, ContainerStatus>.Succeed(container);
    }
}

/// <summary>A container's document counts by status, chunks, size and embedding models. Open to viewers.</summary>
[Operation("containers.stats", Scope = "knowledge:read", Risk = RiskTier.Read)]
public sealed class GetContainerStats(IContainerStore containers, IDocumentStore documents, VectorModelDiscovery models, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<ContainerStatsView, ContainerStatus>> RunAsync(OperationContext context, ContainerKey key, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Reader))
            return Attempt<ContainerStatsView, ContainerStatus>.Fail(ContainerStatus.Forbidden, "You don't have permission to view containers.");

        var container = await ContainerRules.FindAsync(containers, key, ct);
        if (container is null || !Guid.TryParse(container.Id, out var id))
            return ContainerRules.NotFound<ContainerStatsView>(key);

        var stats = await documents.GetContainerStatsAsync(id, ct);
        var embedding = await models.GetModelsAsync(id, ct);
        return Attempt<ContainerStatsView, ContainerStatus>.Succeed(new ContainerStatsView(container, stats, embedding));
    }
}

/// <summary>Every container operation in one place, for the pages that offer several of them.</summary>
public sealed class ContainerOperations(
    ListContainers list, GetContainer get, CreateContainer create, RenameContainer rename, DeleteContainer delete, GetContainerStats stats,
    GetContainerSettings getSettings, UpdateContainerSettings updateSettings, ResetContainerSettings resetSettings,
    RetryFailedContainerDocuments retryFailed)
{
    public ListContainers List { get; } = list;

    public GetContainer Get { get; } = get;

    public CreateContainer Create { get; } = create;

    public RenameContainer Rename { get; } = rename;

    public DeleteContainer Delete { get; } = delete;

    public GetContainerStats Stats { get; } = stats;

    public GetContainerSettings GetSettings { get; } = getSettings;

    public UpdateContainerSettings UpdateSettings { get; } = updateSettings;

    public ResetContainerSettings ResetSettings { get; } = resetSettings;

    public RetryFailedContainerDocuments RetryFailed { get; } = retryFailed;
}
