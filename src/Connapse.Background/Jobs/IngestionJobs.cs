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
/// Hangfire job handlers for the ingestion pipeline + per-doc summarization.
/// Indexing and summarization are split into separate jobs so they can be queued onto
/// different worker pools (bounded-duration ingestion vs variable-duration LLM calls).
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
    /// How long a document may sit Queued or Processing before the sweep checks on its job. Matches
    /// Hangfire's invisibility timeout, after which Hangfire itself gives up on a silent worker.
    /// </summary>
    internal static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(30);

    /// <summary>Hangfire states in which a job will still run, or is running.</summary>
    private static readonly HashSet<string> LiveJobStates = new(StringComparer.OrdinalIgnoreCase)
    {
        EnqueuedState.StateName, ProcessingState.StateName, ScheduledState.StateName, AwaitingState.StateName,
    };

    private readonly IKnowledgeIngester _ingester;
    private readonly IDocumentStore _docStore;
    private readonly IDocumentLifecycle _lifecycle;
    private readonly IPerDocSummarizer _summarizer;
    private readonly IContainerSettingsResolver _settingsResolver;
    private readonly IBackgroundJobClient _bgClient;
    private readonly IDbContextFactory<KnowledgeDbContext> _dbFactory;
    private readonly JobStorage _jobStorage;
    private readonly IReindexService _reindex;
    private readonly ILogger<IngestionJobs> _logger;

    public IngestionJobs(
        IKnowledgeIngester ingester,
        IDocumentStore docStore,
        IDocumentLifecycle lifecycle,
        IPerDocSummarizer summarizer,
        IContainerSettingsResolver settingsResolver,
        IBackgroundJobClient bgClient,
        IDbContextFactory<KnowledgeDbContext> dbFactory,
        JobStorage jobStorage,
        IReindexService reindex,
        ILogger<IngestionJobs> logger)
    {
        _ingester = ingester;
        _docStore = docStore;
        _lifecycle = lifecycle;
        _summarizer = summarizer;
        _settingsResolver = settingsResolver;
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

        await ScheduleSummaryAsync(id, documentId, options);
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

    /// <summary>
    /// Decides whether a per-doc summary follows. Two modes need none: summaries disabled, and
    /// document-clustering, which summarizes medoids at rollup time instead.
    /// </summary>
    /// <remarks>
    /// Wrap-up uses CancellationToken.None: the document is already Ready, and a restart during
    /// these brief writes should not leave its summary state behind.
    /// </remarks>
    private async Task ScheduleSummaryAsync(Guid id, string documentId, IngestionOptions options)
    {
        SummarySettings? settings = null;
        if (Guid.TryParse(options.ContainerId, out Guid containerId))
            settings = await _settingsResolver.GetSummarySettingsAsync(containerId, CancellationToken.None);

        bool needsPerDocSummary = settings is not null
                                   && settings.Enabled
                                   && settings.ContainerSummaryMethod == SummaryStrategy.SummaryClustering;

        if (!needsPerDocSummary)
        {
            await _lifecycle.SetSummaryStatusAsync(id, SummaryStatus.NotNeeded, CancellationToken.None);
            return;
        }

        await _lifecycle.SetSummaryStatusAsync(id, SummaryStatus.Pending, CancellationToken.None);
        _bgClient.Enqueue<IIngestionJobs>(j => j.PerDocSummaryAsync(documentId, default));
    }

    [Queue(JobQueues.Default)]
    [AutomaticRetry(Attempts = 0)] // The next tick is the retry.
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task RequeueStuckDocumentsAsync(CancellationToken ct)
    {
        DateTime cutoff = DateTime.UtcNow - StuckAfter;

        await using var context = await _dbFactory.CreateDbContextAsync(ct);
        var candidates = await context.Documents
            .AsNoTracking()
            .Where(d => (d.IngestionStatus == DocumentStatus.Queued || d.IngestionStatus == DocumentStatus.Processing)
                        && d.StatusChangedAt < cutoff)
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

        int requeued = await _reindex.RequeueAsync(lost, ct);
        _logger.LogWarning(
            "RequeueStuckDocuments: {Lost} document(s) had lost their ingestion job; re-enqueued {Requeued}",
            lost.Count, requeued);
    }

    [Queue(JobQueues.Summarization)]
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 30, 120, 600 })]
    public async Task PerDocSummaryAsync(string documentId, CancellationToken ct)
    {
        if (!Guid.TryParse(documentId, out Guid id)) return;

        try
        {
            Document? doc = await _docStore.GetAsync(documentId, ct);
            if (doc is null)
            {
                _logger.LogInformation(
                    "PerDocSummarySkipped {DocumentId} reason=document_not_found",
                    LogSanitizer.Sanitize(documentId));
                return;
            }

            if (!Guid.TryParse(doc.ContainerId, out Guid containerId))
            {
                _logger.LogWarning(
                    "PerDocSummarySkipped {DocumentId} reason=invalid_container_id",
                    LogSanitizer.Sanitize(documentId));
                return;
            }

            SummarySettings settings = await _settingsResolver.GetSummarySettingsAsync(containerId, ct);
            if (!settings.Enabled || settings.ContainerSummaryMethod == SummaryStrategy.DocumentClustering)
            {
                // Settings changed since the summary was scheduled. document-clustering
                // summarizes K medoids lazily at rollup time, so no per-doc summary is due.
                _logger.LogInformation(
                    "PerDocSummarySkipped {DocumentId} reason={Reason}",
                    LogSanitizer.Sanitize(documentId),
                    settings.Enabled ? "document_clustering_mode" : "summaries_disabled");
                await _lifecycle.SetSummaryStatusAsync(id, SummaryStatus.NotNeeded, CancellationToken.None);
                return;
            }

            // Rebuilt from the stored chunks rather than re-reading and re-parsing the file:
            // the chunks are the text this document was indexed with, and reading them costs no
            // connector round trip to S3, SFTP or GitHub.
            string? parsedText = await _docStore.GetDocumentTextAsync(documentId, ct);
            if (string.IsNullOrWhiteSpace(parsedText))
            {
                _logger.LogInformation(
                    "PerDocSummarySkipped {DocumentId} reason=empty_parsed_content",
                    LogSanitizer.Sanitize(documentId));
                await _lifecycle.SetSummaryStatusAsync(id, SummaryStatus.NotNeeded, CancellationToken.None);
                return;
            }

            string contentHash = doc.Metadata.GetValueOrDefault("ContentHash") ?? string.Empty;
            PerDocSummarizationResult result = await _summarizer.GenerateAsync(
                documentId, contentHash, parsedText, doc.ContentType, doc.FileName, settings, ct);

            if (result.Skipped)
            {
                _logger.LogInformation(
                    "PerDocSummarySkipped {DocumentId} reason={Reason}",
                    LogSanitizer.Sanitize(documentId),
                    LogSanitizer.Sanitize(result.SkipReason ?? ""));
                await _lifecycle.SetSummaryStatusAsync(id, SummaryStatus.NotNeeded, CancellationToken.None);
                return;
            }

            // Best-effort finalization: the summary is written, so record it even if the app is
            // shutting down.
            await _lifecycle.SetSummaryStatusAsync(id, SummaryStatus.Done, CancellationToken.None);

            // Container rollup is triggered by the recurring SweepStaleContainersAsync job
            // (every 5 minutes) rather than per-doc completion. The sweep coalesces N uploads
            // into 1 rollup once the per-doc burst has settled (no in-flight summary jobs for
            // the container), matching what Postgres materialized views and Algolia derived
            // indexes do for expensive aggregates over frequently-changing base data.

            _logger.LogInformation(
                "PerDocSummaryCompleted {DocumentId} model={Model} inTok={InputTokens} outTok={OutputTokens}",
                LogSanitizer.Sanitize(documentId),
                LogSanitizer.Sanitize(result.Model ?? ""),
                result.InputTokens,
                result.OutputTokens);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only the summary failed. The document stays Ready and searchable.
            _logger.LogWarning(ex,
                "PerDocSummaryAsync threw {DocumentId}", LogSanitizer.Sanitize(documentId));
            await _lifecycle.SetSummaryStatusAsync(id, SummaryStatus.Failed, CancellationToken.None);
            throw;
        }
    }
}
