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
    [AutomaticRetry(Attempts = 0)] // Sweeps are recurring; on failure just wait until next hour.
    public async Task SweepStaleContainersAsync(CancellationToken ct)
    {
        IReadOnlyList<Guid> staleContainerIds =
            await _docStore.FindContainersWithStaleSummariesAsync(ct);

        if (staleContainerIds.Count == 0)
        {
            _logger.LogInformation("SweepStaleContainers: no stale containers");
            return;
        }

        _logger.LogInformation(
            "SweepStaleContainers: enqueueing rollup for {Count} stale containers",
            staleContainerIds.Count);

        foreach (Guid containerId in staleContainerIds)
        {
            _bgClient.Enqueue<ISummaryJobs>(s => s.RollupContainerAsync(containerId, default));
        }
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
