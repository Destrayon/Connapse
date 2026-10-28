using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Application.Files;

/// <summary>Why a batch status read didn't succeed.</summary>
public enum BatchStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(404, "batch-not-found", "Batch not found")]
    NotFound,
}

/// <summary>Where an upload's ingestion stands: Queued, Processing, Completed or Failed.</summary>
public sealed record BatchProgress(string JobId, string DocumentId, string ContainerId, string State, int PercentComplete, string? ErrorMessage);

/// <summary>
/// Reads the progress of the ingestion job an upload returned. The job names its document, and the document's row
/// holds the status, which outlives the job and any retries that replaced it. A job whose document the caller may not
/// read is answered as missing, as files.get answers the document. Open to viewers and reading agent keys.
/// </summary>
[Operation("batches.status", Scope = "knowledge:read", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class GetBatchStatus(IIngestionJobs jobs, IDocumentStore documents, IDocumentAccess access, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<BatchProgress, BatchStatus>> RunAsync(OperationContext context, string jobId, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Reader))
            return Attempt<BatchProgress, BatchStatus>.Fail(BatchStatus.Forbidden, "You don't have permission to view ingestion progress.");

        string? documentId = string.IsNullOrWhiteSpace(jobId) ? null : await jobs.DocumentIdAsync(jobId, ct);
        var document = documentId is null ? null : await documents.GetAsync(documentId, ct);
        if (document is null || !await access.CanReadAsync(context.User, document, ct))
            return Attempt<BatchProgress, BatchStatus>.Fail(BatchStatus.NotFound, $"Batch {jobId} not found");

        return Attempt<BatchProgress, BatchStatus>.Succeed(new BatchProgress(
            jobId,
            document.Id,
            document.ContainerId,
            document.Status switch
            {
                DocumentStatus.Queued => "Queued",
                DocumentStatus.Processing => "Processing",
                DocumentStatus.Ready => "Completed",
                _ => "Failed",
            },
            document.Status == DocumentStatus.Ready ? 100 : 0,
            document.Metadata.GetValueOrDefault("ErrorMessage")));
    }
}

