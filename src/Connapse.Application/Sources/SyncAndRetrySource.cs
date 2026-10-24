using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Application.Sources;

/// <summary>A sync cycle's outcome, and the source it ran for.</summary>
public sealed record SourceSynced(Source Source, SourceSyncResult Result);

/// <summary>
/// Runs one sync cycle for a source now. Administrators only. A paused source, or one whose connection
/// is gone, is refused rather than run; a cycle already in progress is a conflict, not a success, so a
/// caller doesn't read "0 upserted" as "nothing to do".
/// </summary>
[Operation("sources.sync", Scope = "admin:sources", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class SyncSource(
    ISourceStore sources, IConnectionStore connections, ISourceSync sync, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<SourceSynced, SourceWriteStatus>> RunAsync(
        OperationContext context, Guid sourceId, bool applyWithheldDeletions = false, CancellationToken ct = default)
    {
        static Attempt<SourceSynced, SourceWriteStatus> Fail(SourceWriteStatus status, string detail) =>
            Attempt<SourceSynced, SourceWriteStatus>.Fail(status, detail);

        if (!await authorizer.AllowsAsync(context.User, Policies.Admin))
            return Fail(SourceWriteStatus.Forbidden, "You don't have permission to sync sources.");

        var source = await sources.GetAsync(sourceId, ct);
        if (source is null)
            return Fail(SourceWriteStatus.NotFound, $"Source {sourceId} not found");
        if (!source.Enabled)
            return Fail(SourceWriteStatus.Invalid, $"Source '{source.Name}' is disabled");

        // A connection-less source (a GitHub source added before App connections) syncs through its
        // Provider; only a source that names a connection can be missing one.
        Connection? connection = null;
        if (source.ConnectionId is Guid connectionId)
        {
            connection = await connections.GetAsync(connectionId, ct);
            if (connection is null)
                return Fail(SourceWriteStatus.Invalid, $"Source '{source.Name}' references a missing connection");
        }
        else if (source.Provider is null)
        {
            return Fail(SourceWriteStatus.Invalid, $"Source '{source.Name}' has neither a connection nor a provider");
        }

        var result = await sync.SyncNowAsync(source, connection, applyWithheldDeletions, ct);
        if (result.AlreadyRunning)
            return Fail(SourceWriteStatus.Busy, $"A sync for source '{source.Name}' is already in progress.");

        await audit.LogAsync(applyWithheldDeletions ? "source.deletions_applied" : "source.synced", "source", source.Id.ToString(),
            new { source.Name, result.Upserted, result.Deleted, result.WithheldDeletions }, ct);
        return Attempt<SourceSynced, SourceWriteStatus>.Succeed(new SourceSynced(source, result));
    }
}

/// <summary>
/// Re-enqueues every document of a source whose ingestion failed, with a fresh attempt budget, rather
/// than waiting for the retry cooldown or a change to the file. Administrators only.
/// </summary>
[Operation("sources.retryFailed", Scope = "admin:sources", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class RetryFailedDocuments(ISourceStore sources, IServiceScopeFactory scopes, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    /// <summary>How many failed documents were queued again.</summary>
    public async Task<Attempt<int, SourceWriteStatus>> RunAsync(OperationContext context, Guid sourceId, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Admin))
            return Attempt<int, SourceWriteStatus>.Fail(SourceWriteStatus.Forbidden, "You don't have permission to retry a source's documents.");

        var source = await sources.GetAsync(sourceId, ct);
        if (source is null)
            return Attempt<int, SourceWriteStatus>.Fail(SourceWriteStatus.NotFound, $"Source {sourceId} not found");

        // Its own scope: the reindex service holds a DbContext, which a Blazor circuit must not share
        // across its handlers, and the page calls this operation from one.
        int enqueued;
        await using (var scope = scopes.CreateAsyncScope())
            enqueued = await scope.ServiceProvider.GetRequiredService<IReindexService>().RetryFailedAsync(source.Id, ct);

        await audit.LogAsync("source.retry_failed", "source", source.Id.ToString(), new { source.Name, enqueued }, ct);
        return Attempt<int, SourceWriteStatus>.Succeed(enqueued);
    }
}
