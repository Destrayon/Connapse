using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Connapse.Application.Containers;

/// <summary>
/// What a container reindex asks for. A null field takes the service's default: no force, settings
/// changes detected, the stored strategy, every document.
/// </summary>
public sealed record ContainerReindexQuery(
    bool? Force = null,
    bool? DetectSettingsChanges = null,
    ChunkingStrategy? Strategy = null,
    bool? DryRun = null,
    int? MaxDocuments = null,
    string? ContinueAfter = null);

/// <summary>What a reconcile found in managed storage and queued.</summary>
public sealed record ContainerReconcileResult(string BatchId, int TotalFiles, int EnqueuedCount, int SkippedCount);

/// <summary>A summary rollup queued for a container, by its background job's id.</summary>
public sealed record SummaryRegeneration(string JobId);

/// <summary>Reindexes a container's documents, in pages when asked. Editors and above.</summary>
[Operation("containers.reindex", Scope = "knowledge:write", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class ReindexContainer(IContainerStore containers, IReindexService reindex, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<ReindexResult, ContainerStatus>> RunAsync(
        OperationContext context, Guid containerId, ContainerReindexQuery query, CancellationToken ct = default)
    {
        static Attempt<ReindexResult, ContainerStatus> Fail(ContainerStatus status, string detail, string? field = null) =>
            Attempt<ReindexResult, ContainerStatus>.Fail(status, detail, field);

        if (!await authorizer.AllowsAsync(context.User, Policies.Writer))
            return Fail(ContainerStatus.Forbidden, "You don't have permission to reindex containers.");
        if (!await containers.ExistsAsync(containerId, ct))
            return ContainerRules.NotFound<ReindexResult>(ContainerKey.ById(containerId));
        if (query.MaxDocuments is <= 0)
            return Fail(ContainerStatus.InvalidReindex, "maxDocuments must be a positive number", "maxDocuments");
        if (query.ContinueAfter is not null && !Guid.TryParse(query.ContinueAfter, out _))
            return Fail(ContainerStatus.InvalidReindex, "continueAfter must be a document id from a previous reindex response", "continueAfter");

        return Attempt<ReindexResult, ContainerStatus>.Succeed(await reindex.ReindexAsync(new ReindexOptions
        {
            ContainerId = containerId.ToString(),
            Force = query.Force ?? false,
            DetectSettingsChanges = query.DetectSettingsChanges ?? true,
            Strategy = query.Strategy,
            DryRun = query.DryRun ?? false,
            MaxDocuments = query.MaxDocuments,
            ContinueAfter = query.ContinueAfter,
        }, ct));
    }
}

/// <summary>Re-enqueues every document in a container whose ingestion failed, with a fresh attempt budget. Editors and above.</summary>
[Operation("containers.retryFailed", Scope = "knowledge:write", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class RetryFailedContainerDocuments(IContainerStore containers, IServiceScopeFactory scopes, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    /// <returns>How many documents were enqueued.</returns>
    public async Task<Attempt<int, ContainerStatus>> RunAsync(OperationContext context, Guid containerId, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Writer))
            return Attempt<int, ContainerStatus>.Fail(ContainerStatus.Forbidden, "You don't have permission to retry documents.");
        if (!await containers.ExistsAsync(containerId, ct))
            return ContainerRules.NotFound<int>(ContainerKey.ById(containerId));

        // Its own scope: the reindex service holds a DbContext, which a Blazor circuit must not share
        // across its handlers, and the file browser calls this operation from one.
        int enqueued;
        await using (var scope = scopes.CreateAsyncScope())
            enqueued = await scope.ServiceProvider.GetRequiredService<IReindexService>().RetryFailedAsync(containerId, ct);
        await audit.LogAsync("container.retry_failed", "container", containerId.ToString(), new { enqueued }, ct);
        return Attempt<int, ContainerStatus>.Succeed(enqueued);
    }
}

/// <summary>
/// Reconciles a container's managed storage with its documents: every stored object with no document
/// is registered and queued. Editors and above. Objects can land in the bucket out of band, and this
/// is the only way to pick them up.
/// </summary>
[Operation("containers.reconcile", Scope = "knowledge:write", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class ReconcileContainer(
    IContainerStore containers,
    IManagedStorageProvider managedStorage,
    IDocumentStore documents,
    IIngestionQueue queue,
    IOptionsMonitor<ChunkingSettings> chunking,
    IAuditLogger audit,
    IOperationAuthorizer authorizer)
{
    public async Task<Attempt<ContainerReconcileResult, ContainerStatus>> RunAsync(OperationContext context, Guid containerId, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Writer))
            return Attempt<ContainerReconcileResult, ContainerStatus>.Fail(ContainerStatus.Forbidden, "You don't have permission to reconcile containers.");
        var container = await containers.GetAsync(containerId, ct);
        if (container is null)
            return ContainerRules.NotFound<ContainerReconcileResult>(ContainerKey.ById(containerId));

        // Managed storage directly, rather than through IConnectorFactory. A container has
        // no connector type to dispatch on any more, and the cloud-scope check that used to
        // guard this route only ever fired for S3 containers — which are
        // sources now, synced by SourceSyncService. Kept because objects can still land in
        // the bucket out of band, and this is the only route that picks them up.
        var connector = managedStorage.CreateConnector(container.Id);
        var remoteFiles = await connector.ListFilesAsync(ct: ct);

        var existingByPath = (await documents.ListAsync(containerId, take: int.MaxValue, ct: ct)).ToDictionary(d => d.Path);

        var batchId = Guid.NewGuid().ToString();
        int enqueued = 0, skipped = 0;

        var strategy = Enum.TryParse<ChunkingStrategy>(chunking.CurrentValue.Strategy, ignoreCase: true, out var parsed)
            ? parsed
            : ChunkingStrategy.Recursive;

        // Collect jobs to enqueue, pre-registering new documents in the DB first
        var jobsToEnqueue = new List<IngestionJob>();

        foreach (var file in remoteFiles)
        {
            var virtualPath = file.Path.StartsWith('/') ? file.Path : "/" + file.Path;

            // Only files with no document are new. A document that is in flight already has
            // a job (or the stuck-job sweep will give it one), and enqueueing it again would
            // supersede that job and repeat its work.
            if (existingByPath.ContainsKey(virtualPath))
            {
                skipped++;
                continue;
            }

            var fileName = Path.GetFileName(virtualPath);
            var documentId = Guid.NewGuid().ToString();
            var contentType = ContentTypeOf(fileName);

            // Pre-register new documents so they appear in the file browser immediately
            await documents.StoreAsync(new Document(
                Id: documentId,
                ContainerId: container.Id,
                FileName: fileName,
                ContentType: contentType,
                Path: virtualPath,
                SizeBytes: file.SizeBytes,
                CreatedAt: DateTime.UtcNow,
                Metadata: new Dictionary<string, string>
                {
                    ["Source"] = "ConnectorSync",
                    ["SyncedAt"] = DateTime.UtcNow.ToString("O")
                }), ct);

            jobsToEnqueue.Add(new IngestionJob(
                DocumentId: documentId,
                Options: new IngestionOptions(
                    DocumentId: documentId,
                    FileName: fileName,
                    ContentType: contentType,
                    ContainerId: container.Id,
                    Path: virtualPath,
                    Strategy: strategy,
                    Metadata: new Dictionary<string, string>
                    {
                        ["OriginalFileName"] = fileName,
                        ["Source"] = "ConnectorSync",
                        ["SyncedAt"] = DateTime.UtcNow.ToString("O")
                    }),
                BatchId: batchId));

            enqueued++;
        }

        foreach (var job in jobsToEnqueue)
            await queue.EnqueueAsync(job, ct);

        await audit.LogAsync("container.synced", "container", containerId.ToString(),
            new { enqueued, skipped, total = remoteFiles.Count }, ct);

        return Attempt<ContainerReconcileResult, ContainerStatus>.Succeed(
            new ContainerReconcileResult(batchId, remoteFiles.Count, enqueued, skipped));
    }

    private static string? ContentTypeOf(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".html" or ".htm" => "text/html",
            ".csv" => "text/csv",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".eml" => "message/rfc822",
            ".msg" => "application/vnd.ms-outlook",
            ".epub" => "application/epub+zip",
            _ => null
        };
}

/// <summary>Queues a rollup of a container's summary from its documents' summaries. Administrators, as the Summary tab has always required.</summary>
[Operation("containers.summaries.regenerate", Scope = "knowledge:write", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class RegenerateContainerSummaries(IContainerStore containers, ISummaryRollup rollup, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<SummaryRegeneration, ContainerStatus>> RunAsync(OperationContext context, Guid containerId, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Admin))
            return Attempt<SummaryRegeneration, ContainerStatus>.Fail(ContainerStatus.Forbidden, "You don't have permission to regenerate summaries.");
        if (!await containers.ExistsAsync(containerId, ct))
            return ContainerRules.NotFound<SummaryRegeneration>(ContainerKey.ById(containerId));

        string jobId = rollup.Enqueue(containerId);
        await audit.LogAsync("container.summaries_regenerated", "container", containerId.ToString(), new { jobId }, ct);
        return Attempt<SummaryRegeneration, ContainerStatus>.Succeed(new SummaryRegeneration(jobId));
    }
}

/// <summary>
/// Clears a container's summary and every document summary in it; the next sweep writes them again.
/// Administrators, as the Summary tab has always required.
/// </summary>
[Operation("containers.summaries.clear", Scope = "knowledge:write", Risk = RiskTier.Destructive, Mcp = McpExposure.Generic)]
public sealed class ClearContainerSummaries(IContainerStore containers, IDocumentStore documents, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    /// <returns>How many document summaries were cleared.</returns>
    public async Task<Attempt<int, ContainerStatus>> RunAsync(OperationContext context, Guid containerId, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Admin))
            return Attempt<int, ContainerStatus>.Fail(ContainerStatus.Forbidden, "You don't have permission to clear summaries.");
        if (!await containers.ExistsAsync(containerId, ct))
            return ContainerRules.NotFound<int>(ContainerKey.ById(containerId));

        int cleared = await documents.ClearDocumentSummariesAsync(containerId, ct);
        await containers.UpdateSummaryAsync(containerId, null, null, null, ct);
        await audit.LogAsync("container.summaries_cleared", "container", containerId.ToString(), new { cleared }, ct);
        return Attempt<int, ContainerStatus>.Succeed(cleared);
    }
}
