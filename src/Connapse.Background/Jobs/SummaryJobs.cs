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
    private const int LazyStuffThreshold = 30; // mirrors ContainerSummarizer.StuffThreshold
    private const int LazyMaxClusters = 20;    // mirrors ContainerSummarizer.MaxClusters

    private readonly IContainerStore _containerStore;
    private readonly IDocumentStore _docStore;
    private readonly IContainerSettingsResolver _settingsResolver;
    private readonly IDocumentSummaryEmbeddingProvider _embeddingProvider;
    private readonly IVectorStore _vectorStore;
    private readonly IPerDocSummarizer _perDocSummarizer;
    private readonly IConnectorFactory _connectorFactory;
    private readonly IEnumerable<IDocumentParser> _parsers;
    private readonly SummaryLlmResolver _llmResolver;
    private readonly ITokenCounter _tokenCounter;
    private readonly IBackgroundJobClient _bgClient;
    private readonly ILogger<SummaryJobs> _logger;

    public SummaryJobs(
        IContainerStore containerStore,
        IDocumentStore docStore,
        IContainerSettingsResolver settingsResolver,
        IDocumentSummaryEmbeddingProvider embeddingProvider,
        IVectorStore vectorStore,
        IPerDocSummarizer perDocSummarizer,
        IConnectorFactory connectorFactory,
        IEnumerable<IDocumentParser> parsers,
        SummaryLlmResolver llmResolver,
        ITokenCounter tokenCounter,
        IBackgroundJobClient bgClient,
        ILogger<SummaryJobs> logger)
    {
        _containerStore = containerStore;
        _docStore = docStore;
        _settingsResolver = settingsResolver;
        _embeddingProvider = embeddingProvider;
        _vectorStore = vectorStore;
        _perDocSummarizer = perDocSummarizer;
        _connectorFactory = connectorFactory;
        _parsers = parsers;
        _llmResolver = llmResolver;
        _tokenCounter = tokenCounter;
        _bgClient = bgClient;
        _logger = logger;
    }

    [Queue(JobQueues.Summarization)]
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    // No Hangfire retry: the recurring SweepStaleContainersAsync (every 5 min) re-enqueues any
    // container that is still stale, so a failed rollup is naturally retried on the next tick —
    // the sweep IS the retry loop. Hangfire retries would instead stack Scheduled jobs on top of
    // the sweep's enqueues; that duplicate pile-up is what exhausted the Postgres connection pool
    // under concurrent rollups. A failed rollup lands in Failed (not Scheduled), which also keeps
    // the sweep's in-flight dedup simple (it only has to skip Enqueued/Processing, never retries).
    [AutomaticRetry(Attempts = 0)]
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

        if (settings.ContainerSummaryMethod == SummaryStrategy.DocumentClustering)
        {
            await RollupDocumentClusteringAsync(containerId, container, settings, ct);
        }
        else
        {
            await RollupSummaryClusteringAsync(containerId, container, settings, ct);
        }
    }

    private async Task RollupSummaryClusteringAsync(
        Guid containerId, Container container, SummarySettings settings, CancellationToken ct)
    {
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
            // doc_set_hash_match: set of summarized docs + their content hashes hasn't changed
            // since the last rollup.
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
            "ContainerRollupCompleted {ContainerId} method=summary-clustering regime={Regime} N={N} k={K} inTok={InTok} outTok={OutTok}",
            LogSanitizer.Sanitize(containerId.ToString()),
            LogSanitizer.Sanitize(result.Regime ?? ""),
            result.NumDocs,
            result.KClusters,
            result.InputTokens,
            result.OutputTokens);
    }

    private async Task RollupDocumentClusteringAsync(
        Guid containerId, Container container, SummarySettings settings, CancellationToken ct)
    {
        IReadOnlyList<Document> allDocs = await _docStore.ListAsync(
            containerId, pathPrefix: null, skip: 0, take: 10_000, ct);

        if (allDocs.Count == 0)
        {
            await _containerStore.UpdateSummaryAsync(containerId, null, null, null, ct);
            return;
        }

        // Hash gate: skip rollup if the (docId, content_hash) set is unchanged since last rollup.
        string docSetHash = ComputeDocSetHash(allDocs);
        if (docSetHash == container.SummaryDocSetHash)
        {
            _logger.LogInformation(
                "ContainerRollupSkipped {ContainerId} reason=doc_set_hash_match",
                LogSanitizer.Sanitize(containerId.ToString()));
            return;
        }

        // Pick which docs to summarize. Below the stuff threshold we summarize all of them;
        // above it we cluster on pooled chunk embeddings and pick K medoids.
        IReadOnlyList<Document> docsToSummarize;
        string regime;
        int? kClusters = null;

        if (allDocs.Count <= LazyStuffThreshold)
        {
            docsToSummarize = allDocs;
            regime = "stuff";
        }
        else
        {
            IReadOnlyList<(Guid DocumentId, float[] Embedding)> pooled =
                await _vectorStore.GetPooledDocumentEmbeddingsAsync(containerId, ct);
            if (pooled.Count == 0)
            {
                _logger.LogInformation(
                    "ContainerRollupSkipped {ContainerId} reason=no_pooled_embeddings",
                    LogSanitizer.Sanitize(containerId.ToString()));
                return;
            }

            int k = Math.Min(LazyMaxClusters, (int)Math.Ceiling(pooled.Count / 3.0));
            kClusters = k;

            // MedoidSelector takes (Guid Id, float[] Embedding); the pooled tuple's element
            // name differs (DocumentId) but the shape matches, so project explicitly to keep
            // the intent obvious.
            var medoidInput = pooled.Select(p => (Id: p.DocumentId, Embedding: p.Embedding)).ToList();
            IReadOnlyList<(Guid Id, float[] Embedding)> medoids =
                MedoidSelector.SelectFarthestFirst(medoidInput, k);
            var medoidIds = medoids.Select(m => m.Id).ToHashSet();

            // Map medoid Guids back to Document instances. Skip any whose docs no longer exist
            // (deleted between pooling query and now) — defensive.
            Dictionary<Guid, Document> docsById = allDocs
                .Where(d => Guid.TryParse(d.Id, out _))
                .ToDictionary(d => Guid.Parse(d.Id));
            docsToSummarize = medoidIds
                .Where(id => docsById.ContainsKey(id))
                .Select(id => docsById[id])
                .ToList();
            regime = "cluster";
        }

        // Lazy-summarize each selected doc: cache hit when content_hash matches; otherwise call LLM.
        var summarizedDocs = new List<DocumentWithSummary>();
        foreach (Document doc in docsToSummarize)
        {
            ct.ThrowIfCancellationRequested();

            string contentHash = doc.Metadata?.GetValueOrDefault("ContentHash") ?? string.Empty;
            bool cacheHit = !string.IsNullOrEmpty(doc.Summary)
                            && !string.IsNullOrEmpty(doc.SummaryContentHash)
                            && doc.SummaryContentHash == contentHash;

            string? summary;
            if (cacheHit)
            {
                summary = doc.Summary;
                _logger.LogDebug(
                    "LazyMedoidSummaryCacheHit {DocumentId}",
                    LogSanitizer.Sanitize(doc.Id));
            }
            else
            {
                summary = await GenerateAndCacheSummaryAsync(doc, settings, ct);
                if (summary is null) continue;
            }

            // The ContainerSummarizer reduce step needs an Embedding too. Empty array is fine
            // because docsToSummarize is already <= LazyMaxClusters, so the reduce step takes
            // its stuff path (N <= StuffThreshold) and never touches .Embedding.
            if (!Guid.TryParse(doc.Id, out Guid docGuid)) continue;
            summarizedDocs.Add(new DocumentWithSummary(
                Id: docGuid,
                Summary: summary!,
                Embedding: Array.Empty<float>()));
        }

        if (summarizedDocs.Count == 0)
        {
            _logger.LogInformation(
                "ContainerRollupSkipped {ContainerId} reason=no_summarizable_docs",
                LogSanitizer.Sanitize(containerId.ToString()));
            return;
        }

        ILlmProvider? llm = _llmResolver.Resolve(settings);
        IContainerSummarizer summarizer = new ContainerSummarizer(llm, _tokenCounter);
        ContainerSummarizationResult result = await summarizer.GenerateAsync(
            container.Name, summarizedDocs, settings, ct);

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
            "ContainerRollupCompleted {ContainerId} method=document-clustering regime={Regime} N={N} k={K} inTok={InTok} outTok={OutTok}",
            LogSanitizer.Sanitize(containerId.ToString()),
            regime,
            allDocs.Count,
            kClusters,
            result.InputTokens,
            result.OutputTokens);
    }

    /// <summary>
    /// Runs the per-doc summarizer for one document in document-clustering mode. The summarizer
    /// persists the result to <c>documents.summary</c> keyed on the doc's content hash, which
    /// serves as the cache for future rollups. Returns null if the doc could not be summarized
    /// (parser failure, empty content, etc).
    /// </summary>
    private async Task<string?> GenerateAndCacheSummaryAsync(
        Document doc, SummarySettings settings, CancellationToken ct)
    {
        if (!Guid.TryParse(doc.ContainerId, out Guid containerId)) return null;
        Container? container = await _containerStore.GetAsync(containerId, ct);
        if (container is null) return null;

        // Re-parse doc text through the container's connector — same pattern as
        // IngestionJobs.PerDocSummaryAsync.
        string parsedText;
        try
        {
            IConnector connector = _connectorFactory.Create(container);
            string jobPath = connector.ResolveJobPath(doc.Path.TrimStart('/'));
            await using Stream stream = await connector.ReadFileAsync(jobPath, ct);

            string extension = Path.GetExtension(doc.FileName).ToLowerInvariant();
            IDocumentParser? parser = _parsers.FirstOrDefault(p => p.SupportedExtensions.Contains(extension));
            if (parser is null)
            {
                _logger.LogInformation(
                    "LazyMedoidSummarySkipped {DocumentId} reason=no_parser_for_extension",
                    LogSanitizer.Sanitize(doc.Id));
                return null;
            }

            ParsedDocument parsed = await parser.ParseAsync(stream, doc.FileName, ct);
            parsedText = parsed.Content;
        }
        catch (FileNotFoundException)
        {
            _logger.LogWarning(
                "LazyMedoidSummarySkipped {DocumentId} reason=file_not_found",
                LogSanitizer.Sanitize(doc.Id));
            return null;
        }

        if (string.IsNullOrWhiteSpace(parsedText))
        {
            _logger.LogInformation(
                "LazyMedoidSummarySkipped {DocumentId} reason=empty_parsed_content",
                LogSanitizer.Sanitize(doc.Id));
            return null;
        }

        // Pass the canonical content hash so PerDocSummarizer persists it as summary_content_hash.
        // That single write is the cache the next rollup's cache-check (SummaryContentHash ==
        // ContentHash) compares against — no second write needed here.
        string contentHash = doc.Metadata?.GetValueOrDefault("ContentHash") ?? string.Empty;
        PerDocSummarizationResult result = await _perDocSummarizer.GenerateAsync(
            doc.Id, contentHash, parsedText, doc.ContentType, doc.FileName, settings, ct);

        if (result.Skipped || string.IsNullOrEmpty(result.Summary))
        {
            _logger.LogInformation(
                "LazyMedoidSummarySkipped {DocumentId} reason={Reason}",
                LogSanitizer.Sanitize(doc.Id),
                LogSanitizer.Sanitize(result.SkipReason ?? "no_summary_returned"));
            return null;
        }

        return result.Summary;
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
    /// Deterministic hash of the (sorted) set of {docId, content_hash} pairs for the docs
    /// in this container. Works for both <c>summary-clustering</c> (docs filtered to those
    /// with summaries — content_hash still identifies stale-vs-fresh state) and
    /// <c>document-clustering</c> (most docs have null summaries; content_hash is the only
    /// durable identifier).
    /// </summary>
    /// <remarks>
    /// Migration impact: hashes differ from the prior (docId, summary-sha256) formula across
    /// the deploy boundary, causing exactly one extra rollup per container on first run
    /// post-deploy. Accepted one-shot cost.
    /// </remarks>
    internal static string ComputeDocSetHash(IEnumerable<Document> docs)
    {
        IEnumerable<string> parts = docs
            .OrderBy(d => d.Id)
            .Select(d =>
            {
                string contentHash = d.Metadata?.GetValueOrDefault("ContentHash") ?? string.Empty;
                return $"{d.Id}|{contentHash}";
            });
        return HexHash.Sha256(string.Join("\n", parts));
    }
}
