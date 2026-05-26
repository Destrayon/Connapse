using System.Collections.Concurrent;
using Connapse.Background.Jobs;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Hangfire;
using Hangfire.States;

namespace Connapse.Background.Storage;

/// <summary>
/// Hangfire-backed IIngestionQueue. Replaces the in-memory Channel-based IngestionQueue.
/// Preserves the existing IIngestionQueue interface so the ~12 call sites (endpoints,
/// MCP tools, UploadService, ConnectorWatcherService, ReindexService) need no changes.
///
/// Job-status tracking + document-to-job mapping remain in-process (lost on restart,
/// same as before) — Hangfire's own job state is the persisted source of truth.
/// </summary>
public sealed class HangfireIngestionQueue : IIngestionQueue
{
    private readonly IBackgroundJobClient _bgClient;
    private readonly ConcurrentDictionary<string, IngestionJobStatus> _jobStatuses = new();
    private readonly ConcurrentDictionary<string, string> _documentToJobId = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobCancellationTokens = new();
    private readonly ConcurrentDictionary<string, string> _jobIdToHangfireParentId = new();

    public HangfireIngestionQueue(IBackgroundJobClient bgClient)
    {
        _bgClient = bgClient;
    }

    /// <summary>
    /// Hangfire doesn't expose a synchronous queue-depth count without going through
    /// JobStorage.Current.GetMonitoringApi(). Operators should consult the Hangfire
    /// dashboard at /hangfire for real-time queue depth.
    /// </summary>
    public int QueueDepth => 0;

    public Task EnqueueAsync(IngestionJob job, CancellationToken cancellationToken = default)
    {
        _jobStatuses[job.JobId] = new IngestionJobStatus(
            JobId: job.JobId,
            DocumentId: job.DocumentId,
            ContainerId: job.Options.ContainerId,
            State: IngestionJobState.Queued,
            CurrentPhase: null,
            PercentComplete: 0,
            ErrorMessage: null,
            StartedAt: null,
            CompletedAt: null);

        _documentToJobId[job.DocumentId] = job.JobId;

        // Enqueue ingestion; ContinueJobWith fires per-doc summary on success
        string parentId = _bgClient.Enqueue<IIngestionJobs>(
            j => j.IngestAsync(job.DocumentId, job.Options, default));
        _bgClient.ContinueJobWith<IIngestionJobs>(
            parentId,
            j => j.PerDocSummaryAsync(job.DocumentId, default));

        _jobIdToHangfireParentId[job.JobId] = parentId;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Hangfire's server processes jobs internally; no consumer-side dequeue is needed.
    /// This method exists on IIngestionQueue for backward compatibility with the prior
    /// Channel-based queue. Returns null to signal "use Hangfire."
    /// </summary>
    public Task<IngestionJob?> DequeueAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IngestionJob?>(null);

    public Task<IngestionJobStatus?> GetStatusAsync(string jobId)
    {
        _jobStatuses.TryGetValue(jobId, out var status);
        return Task.FromResult(status);
    }

    public void UpdateJobStatus(
        string jobId,
        IngestionJobState state,
        IngestionPhase? currentPhase = null,
        double percentComplete = 0,
        string? errorMessage = null)
    {
        if (_jobStatuses.TryGetValue(jobId, out var currentStatus))
        {
            _jobStatuses[jobId] = currentStatus with
            {
                State = state,
                CurrentPhase = currentPhase,
                PercentComplete = percentComplete,
                ErrorMessage = errorMessage,
                CompletedAt = state is IngestionJobState.Completed or IngestionJobState.Failed
                    ? DateTime.UtcNow
                    : currentStatus.CompletedAt
            };
        }
    }

    public IReadOnlyDictionary<string, IngestionJobStatus> GetAllStatuses() => _jobStatuses;

    public void RegisterJobCancellation(string jobId, CancellationTokenSource cts)
    {
        _jobCancellationTokens[jobId] = cts;
    }

    public void UnregisterJobCancellation(string jobId)
    {
        if (_jobCancellationTokens.TryRemove(jobId, out var cts))
            cts.Dispose();
    }

    public Task<bool> CancelJobForDocumentAsync(string documentId)
    {
        if (!_documentToJobId.TryRemove(documentId, out var jobId))
            return Task.FromResult(false);

        bool deleted = false;
        if (_jobIdToHangfireParentId.TryRemove(jobId, out var hangfireParentId))
        {
            _bgClient.ChangeState(
                hangfireParentId,
                new DeletedState(),
                expectedState: null);
            deleted = true;
        }

        if (_jobCancellationTokens.TryRemove(jobId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        if (_jobStatuses.TryGetValue(jobId, out var status))
        {
            _jobStatuses[jobId] = status with
            {
                State = IngestionJobState.Failed,
                ErrorMessage = "Cancelled by user",
                CompletedAt = DateTime.UtcNow
            };
        }

        return Task.FromResult(deleted);
    }
}
