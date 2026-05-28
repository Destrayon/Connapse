using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Ingestion.Summarization;
using Connapse.Storage.Llm;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Connapse.Background.Jobs;

/// <summary>
/// Hangfire job handlers for container-level rollup operations. Mirrors the orchestration
/// logic that previously lived in <c>ContainerSummaryWorker</c>, with the timer/debouncer
/// replaced by Hangfire's <c>Schedule</c> + <c>DisableConcurrentExecution</c>.
/// </summary>
public sealed class SummaryJobs : ISummaryJobs
{
    private readonly IContainerStore _containerStore;
    private readonly IDocumentStore _docStore;
    private readonly IContainerSettingsResolver _settingsResolver;
    private readonly IDocumentSummaryEmbeddingProvider _embeddingProvider;
    private readonly SummaryLlmResolver _llmResolver;
    private readonly ITokenCounter _tokenCounter;
    private readonly IBackgroundJobClient _bgClient;
    private readonly ILogger<SummaryJobs> _logger;

    public SummaryJobs(
        IContainerStore containerStore,
        IDocumentStore docStore,
        IContainerSettingsResolver settingsResolver,
        IDocumentSummaryEmbeddingProvider embeddingProvider,
        SummaryLlmResolver llmResolver,
        ITokenCounter tokenCounter,
        IBackgroundJobClient bgClient,
        ILogger<SummaryJobs> logger)
    {
        _containerStore = containerStore;
        _docStore = docStore;
        _settingsResolver = settingsResolver;
        _embeddingProvider = embeddingProvider;
        _llmResolver = llmResolver;
        _tokenCounter = tokenCounter;
        _bgClient = bgClient;
        _logger = logger;
    }

    [Queue(JobQueues.Summarization)]
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 30, 120, 600 })]
    public async Task RollupContainerAsync(Guid containerId, CancellationToken ct)
    {
        Container? container = await _containerStore.GetAsync(containerId, ct);
        if (container is null) return;

        SummarySettings settings = await _settingsResolver.GetSummarySettingsAsync(containerId, ct);
        if (!settings.Enabled)
        {
            _logger.LogInformation(
                "ContainerRollupSkipped {ContainerId} reason=summaries_disabled",
                LogSanitizer.Sanitize(containerId.ToString()));
            return;
        }

        IReadOnlyList<Document> docs = await _docStore.ListAsync(
            containerId, pathPrefix: null, skip: 0, take: 10_000, ct);
        List<Document> withSummaries = docs.Where(d => !string.IsNullOrEmpty(d.Summary)).ToList();

        if (withSummaries.Count == 0)
        {
            // No summarized docs — clear any existing container summary so we don't keep
            // a stale one around after all docs are deleted.
            await _containerStore.UpdateSummaryAsync(containerId, null, null, null, ct);
            return;
        }

        string docSetHash = ComputeDocSetHash(withSummaries);
        if (docSetHash == container.SummaryDocSetHash)
        {
            // doc_set_hash_match: set of summarized docs + their summary texts hasn't changed
            // since the last rollup. Accepted trade-off: content_hash changes inside
            // already-summarized docs aren't detected by this hash alone.
            _logger.LogInformation(
                "ContainerRollupSkipped {ContainerId} reason=doc_set_hash_match",
                LogSanitizer.Sanitize(containerId.ToString()));
            return;
        }

        ILlmProvider? llm = _llmResolver.Resolve(settings);
        IReadOnlyList<DocumentWithSummary> docsWithEmbeddings =
            await _embeddingProvider.GetSummaryEmbeddingsAsync(withSummaries, ct);

        IContainerSummarizer summarizer = new ContainerSummarizer(llm, _tokenCounter);
        ContainerSummarizationResult result = await summarizer.GenerateAsync(
            container.Name, docsWithEmbeddings, settings, ct);

        if (result.Skipped)
        {
            _logger.LogInformation(
                "ContainerRollupSkipped {ContainerId} reason={Reason}",
                LogSanitizer.Sanitize(containerId.ToString()),
                LogSanitizer.Sanitize(result.SkipReason ?? ""));
            return;
        }

        await _containerStore.UpdateSummaryAsync(
            containerId, result.Summary, DateTime.UtcNow, docSetHash, ct);

        _logger.LogInformation(
            "ContainerRollupCompleted {ContainerId} regime={Regime} N={N} k={K} inTok={InTok} outTok={OutTok}",
            LogSanitizer.Sanitize(containerId.ToString()),
            LogSanitizer.Sanitize(result.Regime ?? ""),
            result.NumDocs,
            result.KClusters,
            result.InputTokens,
            result.OutputTokens);
    }

    [Queue(JobQueues.Summarization)]
    [AutomaticRetry(Attempts = 0)] // Sweeps are recurring; on failure just wait until next tick.
    public async Task SweepStaleContainersAsync(CancellationToken ct)
    {
        IReadOnlyList<Guid> staleContainerIds =
            await _docStore.FindContainersWithStaleSummariesAsync(ct);

        if (staleContainerIds.Count == 0)
        {
            _logger.LogInformation("SweepStaleContainers: no stale containers");
            return;
        }

        // Containers with in-flight PerDocSummary jobs are still "settling" — skip them
        // this tick and let the next sweep pick them up once the per-doc burst is done.
        // This is the debounce mechanism: by waiting for stability instead of triggering
        // on every per-doc completion, a burst of N uploads converges to 1 rollup.
        HashSet<Guid> inFlight = GetContainersWithInFlightPerDocJobs();

        var ready = staleContainerIds.Where(id => !inFlight.Contains(id)).ToList();
        if (ready.Count == 0)
        {
            _logger.LogInformation(
                "SweepStaleContainers: {Stale} stale, all settling; waiting for next sweep",
                staleContainerIds.Count);
            return;
        }

        _logger.LogInformation(
            "SweepStaleContainers: enqueueing rollup for {Ready} settled containers ({Settling} still settling)",
            ready.Count, staleContainerIds.Count - ready.Count);

        foreach (Guid containerId in ready)
        {
            _bgClient.Enqueue<ISummaryJobs>(s => s.RollupContainerAsync(containerId, default));
        }
    }

    /// <summary>
    /// Returns the set of container IDs that currently have a PerDocSummaryAsync job
    /// either enqueued or actively processing. Used by the sweep to wait for a burst
    /// to settle before triggering a rollup.
    /// </summary>
    /// <remarks>
    /// Walks Hangfire's monitoring API. Best-effort: the per-doc job's containerId is
    /// derived by looking up the document in the store. If the lookup fails (deleted
    /// doc, etc.), the job is ignored — worst case we trigger a rollup that the hash
    /// check then short-circuits.
    /// </remarks>
    private HashSet<Guid> GetContainersWithInFlightPerDocJobs()
    {
        var result = new HashSet<Guid>();
        try
        {
            var monitor = JobStorage.Current.GetMonitoringApi();

            // Per-doc summary jobs queue onto the "summarization" queue.
            var enqueued = monitor.EnqueuedJobs(JobQueues.Summarization, 0, 1000);
            var processing = monitor.ProcessingJobs(0, 1000);

            void AddDocContainerIds<TDto>(IEnumerable<KeyValuePair<string, TDto>> jobs, Func<TDto, Hangfire.Common.Job?> getJob)
            {
                foreach (var (_, dto) in jobs)
                {
                    var job = getJob(dto);
                    if (job?.Method.Name != nameof(IIngestionJobs.PerDocSummaryAsync))
                        continue;
                    if (job.Args.Count == 0)
                        continue;
                    // First arg is documentId (string). Look up its container.
                    string? documentId = job.Args[0] as string;
                    if (string.IsNullOrEmpty(documentId))
                        continue;

                    var doc = _docStore.GetAsync(documentId, CancellationToken.None).GetAwaiter().GetResult();
                    if (doc is not null && Guid.TryParse(doc.ContainerId, out Guid cid))
                        result.Add(cid);
                }
            }

            AddDocContainerIds(enqueued, dto => dto.Job);
            AddDocContainerIds(processing, dto => dto.Job);
        }
        catch (Exception ex)
        {
            // Non-fatal: if monitoring API fails, we fall back to "no in-flight jobs"
            // and trigger rollups. The hash check defense-in-depth still short-circuits
            // duplicates.
            _logger.LogWarning(ex, "GetContainersWithInFlightPerDocJobs failed; assuming none");
        }
        return result;
    }

    /// <summary>
    /// Deterministic hash of the (sorted) set of {docId, summary} pairs in this container.
    /// Matches the legacy <c>ContainerSummaryWorker.ComputeDocSetHash</c> exactly so jobs
    /// migrated mid-flight don't trigger spurious re-rollups due to a hash format change.
    /// </summary>
    internal static string ComputeDocSetHash(IEnumerable<Document> docs)
    {
        IEnumerable<string> parts = docs
            .OrderBy(d => d.Id)
            .Select(d => $"{d.Id}|{HexHash.Sha256(d.Summary ?? string.Empty)}");
        return HexHash.Sha256(string.Join("\n", parts));
    }
}
