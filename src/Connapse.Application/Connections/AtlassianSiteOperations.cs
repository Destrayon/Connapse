using Connapse.Core;

namespace Connapse.Application.Connections;

// The /api/v1/atlassian/sites routes, kept as aliases of the connection operations: each runs the
// connection operation it names, with the site's provider required, so the two can't drift apart.

/// <summary>Every Atlassian site, by name: <see cref="ListConnections"/> narrowed to Atlassian.</summary>
[Operation("atlassian.sites.list", Scope = "admin:sources", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class ListAtlassianSites(ListConnections list)
{
    public Task<Attempt<ConnectionList, ConnectionStatus>> RunAsync(OperationContext context, CancellationToken ct = default) =>
        list.RunAsync(context, new ListConnectionsQuery(Provider: ConnectionProvider.Atlassian), ct);
}

/// <summary>One Atlassian site: <see cref="GetConnection"/>, another provider's connection reading as missing.</summary>
[Operation("atlassian.sites.get", Scope = "admin:sources", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class GetAtlassianSite(GetConnection get)
{
    public Task<Attempt<ConnectionView, ConnectionStatus>> RunAsync(OperationContext context, Guid connectionId, CancellationToken ct = default) =>
        get.RunAsync(context, ConnectionRef.AtlassianSite(connectionId), ct);
}

/// <summary>Renames an Atlassian site: <see cref="UpdateConnection"/>.</summary>
[Operation("atlassian.sites.update", Scope = "admin:sources", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class RenameAtlassianSite(UpdateConnection update)
{
    public Task<Attempt<ConnectionView, ConnectionStatus>> RunAsync(OperationContext context, Guid connectionId, string? name, CancellationToken ct = default) =>
        update.RunAsync(context, ConnectionRef.AtlassianSite(connectionId), name, ct);
}

/// <summary>Pauses an Atlassian site: <see cref="PauseConnection"/>.</summary>
[Operation("atlassian.sites.pause", Scope = "admin:sources", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class PauseAtlassianSite(PauseConnection pause)
{
    public Task<Attempt<ConnectionSyncState, ConnectionStatus>> RunAsync(OperationContext context, Guid connectionId, CancellationToken ct = default) =>
        pause.RunAsync(context, ConnectionRef.AtlassianSite(connectionId), ct);
}

/// <summary>Resumes an Atlassian site: <see cref="ResumeConnection"/>.</summary>
[Operation("atlassian.sites.resume", Scope = "admin:sources", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class ResumeAtlassianSite(ResumeConnection resume)
{
    public Task<Attempt<ConnectionSyncState, ConnectionStatus>> RunAsync(
        OperationContext context, Guid connectionId, IReadOnlyCollection<Guid>? sourceIds = null, CancellationToken ct = default) =>
        resume.RunAsync(context, ConnectionRef.AtlassianSite(connectionId), sourceIds, ct);
}

/// <summary>Deletes an Atlassian site: <see cref="DeleteConnection"/>.</summary>
[Operation("atlassian.sites.delete", Scope = "admin:sources", Risk = RiskTier.Destructive, Mcp = McpExposure.Generic)]
public sealed class DeleteAtlassianSite(DeleteConnection delete)
{
    public Task<Attempt<Connection, ConnectionStatus>> RunAsync(OperationContext context, Guid connectionId, string? confirmName, CancellationToken ct = default) =>
        delete.RunAsync(context, ConnectionRef.AtlassianSite(connectionId), confirmName, ct);
}
