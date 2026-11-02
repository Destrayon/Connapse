using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Data;
using Hangfire;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Connapse.Background.Jobs;

/// <summary>
/// Hangfire job handlers for the ingestion pipeline.
/// </summary>
/// <remarks>
/// Retries are decided here rather than by Hangfire's <c>AutomaticRetry</c>, because the budget
/// lives on the document (<c>attempt_count</c>) where the sync engine and the UI can see it. A
/// transient failure with attempts left marks the document Queued and schedules the next attempt
/// with backoff; one with none left marks it failed and rethrows, so the dashboard shows it too.
/// </remarks>
public sealed class IngestionJobs : IIngestionJobs
{
    /// <summary>Attempts per version of a file before the document is marked FailedRetryable.</summary>
    public const int MaxAttempts = 4;

    /// <summary>Delay before attempt N+1, indexed by attempts already made minus one.</summary>
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
    ];

    /// <summary>
    /// How long a document may sit Processing before the sweep checks on its job. Matches
    /// Hangfire's invisibility timeout, after which Hangfire itself gives up on a silent worker.
    /// </summary>
    internal static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a document may sit Queued before the sweep checks on its job. A Queued document is
    /// without a live job only for the moment between its status and its job being written, unless
    /// the enqueue failed (CON-96), so it need not wait as long as a Processing one. A job still
    /// waiting its turn or its retry is live, and is left alone.
    /// </summary>
    internal static readonly TimeSpan QueuedStuckAfter = TimeSpan.FromMinutes(5);

    /// <summary>Hangfire states in which a job will still run, or is running.</summary>
    private static readonly HashSet<string> LiveJobStates = new(StringComparer.OrdinalIgnoreCase)
    {
        EnqueuedState.StateName, ProcessingState.StateName, ScheduledState.StateName, AwaitingState.StateName,
    };

    private readonly IKnowledgeIngester _ingester;
    private readonly IDocumentStore _docStore;
    private readonly IDocumentLifecycle _lifecycle;
    private readonly IBackgroundJobClient _bgClient;
    private readonly IDbContextFactory<KnowledgeDbContext> _dbFactory;
    private readonly JobStorage _jobStorage;
    private readonly IReindexService _reindex;
    private readonly ILogger<IngestionJobs> _logger;

    public IngestionJobs(
        IKnowledgeIngester ingester,
        IDocumentStore docStore,
        IDocumentLifecycle lifecycle,
        IBackgroundJobClient bgClient,
        IDbContextFactory<KnowledgeDbContext> dbFactory,
        JobStorage jobStorage,
        IReindexService reindex,
        ILogger<IngestionJobs> logger)
    {
        _ingester = ingester;
        _docStore = docStore;
        _lifecycle = lifecycle;
        _bgClient = bgClient;
        _dbFactory = dbFactory;
        _jobStorage = jobStorage;
        _reindex = reindex;
        _logger = logger;
    }

    [Queue(JobQueues.Ingestion)]
    [AutomaticRetry(Attempts = 0)]
    public async Task IngestAsync(string documentId, IngestionOptions options, CancellationToken ct)
    {
        if (!Guid.TryParse(documentId, out Guid id))
        {
            _logger.LogWarning("IngestSkipped {DocumentId} reason=invalid_id", LogSanitizer.Sanitize(documentId));
            return;
        }

        int generation = options.Generation;
        if (generation == 0)
        {
            // A job enqueued before generations existed carries 0, which the pipeline reads as a direct caller and
            // which would let it take over a newer upload (CON-102). It claims the generation it finds instead.
            generation = await _lifecycle.CurrentGenerationAsync(id, ct) ?? 0;
            if (generation == 0)
            {
                _logger.LogInformation("IngestSkipped {DocumentId} reason=not_found", LogSanitizer.Sanitize(documentId));
                return;
            }
            options = options with { Generation = generation };
        }

        if (!await _lifecycle.TryClaimAsync(id, generation, ct))
        {
            // Superseded by a newer version, or already taken by another worker. Either way the
            // document has a job that is current, and it is not this one.
            _logger.LogInformation(
                "IngestSkipped {DocumentId} reason=not_claimable generation={Generation}",
                LogSanitizer.Sanitize(documentId), generation);
            return;
        }

        try
        {
            IngestionResult result = await _ingester.IngestByIdAsync(documentId, options, ct);
            if (result.ChunkCount == 0)
                return; // Superseded mid-flight; the newer job owns the document now.
        }
        catch (PermanentIngestionException ex)
        {
            // Retrying cannot help, so no retry is scheduled and nothing is rethrown.
            _logger.LogWarning(
                "IngestFailedPermanently {DocumentId}: {Reason}", LogSanitizer.Sanitize(documentId), ex.Message);
            await _lifecycle.FailAsync(id, generation, DocumentStatus.FailedPermanent, ex.Message, CancellationToken.None);
            return;
        }
        catch (DocumentOwnershipChangedException ex)
        {
            // A job asked to move a document to another owner. That is a caller's bug, not a
            // property of the file, so no retry — but the document is left visibly failed rather
            // than stuck in Processing, where the stuck-job sweep would re-enqueue it for ever.
            _logger.LogError(ex, "IngestRefused {DocumentId}", LogSanitizer.Sanitize(documentId));
            await _lifecycle.FailAsync(id, generation, DocumentStatus.FailedPermanent, ex.Message, CancellationToken.None);
            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The server is shutting down. Hangfire re-runs this same job on the next start, and
            // that run can only claim a Queued document.
            await _lifecycle.RetryScheduledAsync(id, generation, "Interrupted by shutdown", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await HandleTransientFailureAsync(id, documentId, options, ex);
            throw;
        }
    }

    private async Task HandleTransientFailureAsync(Guid id, string documentId, IngestionOptions options, Exception ex)
    {
        Document? doc = await _docStore.GetAsync(documentId, CancellationToken.None);
        int attempts = doc is null ? MaxAttempts : doc.AttemptCount;

        if (attempts >= MaxAttempts)
        {
            _logger.LogWarning(ex,
                "IngestFailed {DocumentId} after {Attempts} attempts", LogSanitizer.Sanitize(documentId), attempts);
            await _lifecycle.FailAsync(
                id, options.Generation, DocumentStatus.FailedRetryable, ex.Message, CancellationToken.None);
            return;
        }

        if (!await _lifecycle.RetryScheduledAsync(id, options.Generation, ex.Message, CancellationToken.None))
            return; // Superseded while this attempt ran; the newer job carries on.

        TimeSpan delay = RetryDelays[Math.Min(attempts, RetryDelays.Length) - 1];
        string retryJobId = _bgClient.Schedule<IIngestionJobs>(j => j.IngestAsync(documentId, options, default), delay);
        await _lifecycle.RecordJobAsync(id, options.Generation, retryJobId, CancellationToken.None);

        _logger.LogWarning(ex,
            "IngestRetryScheduled {DocumentId} attempt={Attempt} delay={Delay}",
            LogSanitizer.Sanitize(documentId), attempts, delay);
    }

    [Queue(JobQueues.Default)]
    [AutomaticRetry(Attempts = 0)] // The next tick is the retry.
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task RequeueStuckDocumentsAsync(CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        DateTime processingCutoff = now - StuckAfter;
        DateTime queuedCutoff = now - QueuedStuckAfter;

        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var candidates = await context.Documents
            .AsNoTracking()
            .Where(d => (d.IngestionStatus == DocumentStatus.Queued && d.StatusChangedAt < queuedCutoff)
                        || (d.IngestionStatus == DocumentStatus.Processing && d.StatusChangedAt < processingCutoff))
            .Select(d => new { d.Id, d.JobId })
            .ToListAsync(ct);

        if (candidates.Count == 0) return;

        var lost = new List<Guid>();
        using (var connection = _jobStorage.GetConnection())
        {
            foreach (var candidate in candidates)
            {
                // No job recorded means the enqueue failed after the status was written.
                string? state = candidate.JobId is null ? null : connection.GetStateData(candidate.JobId)?.Name;
                if (state is null || !LiveJobStates.Contains(state))
                    lost.Add(candidate.Id);
            }
        }

        if (lost.Count == 0) return;

        int requeued = await _reindex.RequeueAsync(lost, resetAttempts: false, ct);
        _logger.LogWarning(
            "RequeueStuckDocuments: {Lost} document(s) had lost their ingestion job; re-enqueued {Requeued}",
            lost.Count, requeued);
    }
}
