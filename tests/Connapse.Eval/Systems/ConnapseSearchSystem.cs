using System.Diagnostics;
using System.Globalization;
using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Eval.Model;
using Connapse.Search.Reranking;
using Connapse.Search.Keyword;
using Connapse.Search.Vector;
using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Connapse.Eval.Systems;

/// <summary>
/// Connapse's own search: documents go through IUploadService (the user upload path, queue and
/// background worker), queries through IKnowledgeSearch. One container per dataset.
/// </summary>
public sealed class ConnapseSearchSystem : ISystemUnderTest
{
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DocumentTimeout = TimeSpan.FromMinutes(2);
    private static readonly IReadOnlyDictionary<string, TimeSpan> NoStages = new Dictionary<string, TimeSpan>();
    private const int UploadBatchSize = 100;

    private readonly EvalHost _host;
    private SystemConfig _config;

    /// <summary>
    /// The search settings the host started with, before any config's were saved: every config's
    /// expected settings are built from this, never from what a previous config left behind.
    /// </summary>
    private SearchSettings? _searchBaseline;
    private readonly TextWriter _log;
    private readonly IEmbeddingProvider? _embeddingOverride;
    private readonly Dictionary<string, (Guid ContainerId, Dictionary<string, string> DocMap)> _datasets = new(StringComparer.Ordinal);

    private ConnapseSearchSystem(EvalHost host, SystemConfig config, TextWriter log, IEmbeddingProvider? embeddingOverride)
    {
        _host = host;
        _config = config;
        _log = log;
        _embeddingOverride = embeddingOverride;
    }

    public string Name => "connapse";

    /// <summary>The in-process host's services, for reading back what ingestion produced.</summary>
    public IServiceProvider Services => _host.Services;

    /// <summary>The container an indexed dataset went into, and its Connapse-to-dataset document IDs.</summary>
    public (Guid ContainerId, IReadOnlyDictionary<string, string> DocMap) ContainerOf(string dataset) =>
        (_datasets[dataset].ContainerId, _datasets[dataset].DocMap);

    public static async Task<ConnapseSearchSystem> StartAsync(
        SystemConfig config, string webContentRoot, EmbeddingDiskCache cache, TextWriter log,
        IEmbeddingProvider? embeddingOverride, CancellationToken ct)
    {
        EvalHost host = await EvalHost.StartAsync(config, webContentRoot, cache, embeddingOverride, ct);
        ConnapseSearchSystem system = new(host, config, log, embeddingOverride);
        system._searchBaseline = host.Services.GetRequiredService<IOptionsMonitor<SearchSettings>>().CurrentValue with { };
        return system;
    }

    public IReadOnlyDictionary<string, string> Describe()
    {
        EmbeddingSettings embedding = _host.Services.GetRequiredService<IOptionsMonitor<EmbeddingSettings>>().CurrentValue;
        SearchSettings search = _host.Services.GetRequiredService<IOptionsMonitor<SearchSettings>>().CurrentValue;
        ChunkingSettings chunking = _host.Services.GetRequiredService<IOptionsMonitor<ChunkingSettings>>().CurrentValue;
        return new Dictionary<string, string>
        {
            ["embedding.provider"] = _embeddingOverride?.GetType().Name ?? embedding.Provider,
            ["embedding.model"] = _embeddingOverride?.ModelId ?? embedding.Model,
            ["embedding.dimensions"] = embedding.Dimensions.ToString(CultureInfo.InvariantCulture),
            ["search.mode"] = _config.SearchMode.ToString(),
            ["search.reranker"] = search.Reranker,
            ["search.crossEncoderModel"] = search.CrossEncoderModel ?? "",
            ["search.fusionAlpha"] = search.FusionAlpha.ToString(CultureInfo.InvariantCulture),
            ["search.hybridCandidatePool"] = search.HybridCandidatePool.ToString(CultureInfo.InvariantCulture),
            ["search.keywordRanker"] = search.KeywordRanker,
            ["search.bm25K1"] = search.Bm25K1.ToString(CultureInfo.InvariantCulture),
            ["search.bm25B"] = search.Bm25B.ToString(CultureInfo.InvariantCulture),
            ["chunking.strategy"] = chunking.Strategy,
            ["chunking.maxChunkSize"] = chunking.MaxChunkSize.ToString(CultureInfo.InvariantCulture),
            ["chunking.overlap"] = chunking.Overlap.ToString(CultureInfo.InvariantCulture),
        };
    }

    public Task<IndexReport> IndexAsync(EvalDataset dataset, CancellationToken ct) =>
        IndexAsync(dataset, IngestionWait.ThrowOnStall, ct);

    /// <summary>
    /// Applies <paramref name="config"/>'s search-time settings the way the Settings page does: saved to
    /// the "search" category, which reloads <see cref="IOptionsMonitor{SearchSettings}"/> without a
    /// restart (#667). The category is reset first, so nothing carries over from the previous config,
    /// and the live settings are checked afterwards: a config that didn't take effect would score the
    /// wrong thing under its name.
    /// </summary>
    public async Task UseSearchConfigAsync(SystemConfig config, CancellationToken ct)
    {
        if (config.IndexKey != _config.IndexKey)
            throw new InvalidOperationException(
                $"{config.Name} needs an index built with different settings than this system was started with.");

        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        ISettingsStore store = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
        IOptionsMonitor<SearchSettings> monitor = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<SearchSettings>>();

        SearchSettings expected = (_searchBaseline ?? throw new InvalidOperationException("No search settings baseline.")) with { };
        new ConfigurationBuilder()
            .AddInMemoryCollection(config.SearchTimeSettings.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build()
            .GetSection("Knowledge:Search")
            .Bind(expected);
        await store.ResetAsync("search", ct);
        await store.SaveAsync("search", expected, ct);
        // A reranker the previous config couldn't reach (or timed out on) must be tried again by this one.
        scope.ServiceProvider.GetRequiredService<RerankerAvailability>().Clear();

        string[] differ = Differences(expected, monitor.CurrentValue);
        if (differ.Length > 0)
            throw new InvalidOperationException(
                $"{config.Name}'s search settings did not take effect: {string.Join(", ", differ)}.");
        _config = config;
    }

    /// <summary>
    /// The properties whose values differ, as "name: expected → actual". A null string and an empty one
    /// count as equal: the settings provider reads a saved null back as an empty string. Key values
    /// are not printed.
    /// </summary>
    public static string[] Differences(SearchSettings expected, SearchSettings actual) =>
        typeof(SearchSettings).GetProperties()
            .Select(p => (p.Name, Expected: Normalize(p.GetValue(expected)), Actual: Normalize(p.GetValue(actual))))
            .Where(x => x.Expected != x.Actual)
            .Select(x => x.Name.Contains("Key", StringComparison.Ordinal)
                ? $"{x.Name} differs"
                : $"{x.Name}: {x.Expected} → {x.Actual}")
            .ToArray();

    private static string Normalize(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    /// <summary>
    /// Uploads every document and waits for ingestion. <see cref="IngestionWait.RecordStalls"/> is for
    /// extract runs, where a document that never settles is a finding: it is recorded as stalled
    /// instead of aborting the run.
    /// </summary>
    public async Task<IndexReport> IndexAsync(EvalDataset dataset, IngestionWait wait, CancellationToken ct)
    {
        if (dataset.Corpus.Any(d => d.Kind == DocumentKind.Image))
            throw new NotSupportedException($"{dataset.Name} contains image documents; ConnapseSearchSystem indexes text only.");

        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        IContainerStore containers = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        IUploadService upload = scope.ServiceProvider.GetRequiredService<IUploadService>();
        IDocumentStore documents = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        Container container = await containers.CreateAsync(new CreateContainerRequest($"eval-{dataset.Name}"), ct);
        Guid containerId = Guid.Parse(container.Id);
        Dictionary<string, string> docMap = new(StringComparer.Ordinal);
        Dictionary<string, string> uploadErrors = new(StringComparer.Ordinal);

        for (int start = 0; start < dataset.Corpus.Count; start += UploadBatchSize)
        {
            List<EvalDocument> batch = dataset.Corpus.Skip(start).Take(UploadBatchSize).ToList();
            List<(EvalDocument Doc, int Index, Stream Stream)> opened = [];
            try
            {
                // A corpus file that cannot be opened is that document's upload error, not the run's.
                for (int i = 0; i < batch.Count; i++)
                {
                    try
                    {
                        opened.Add((batch[i], start + i, OpenContent(batch[i])));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        uploadErrors[batch[i].Id] = $"could not open the file: {ex.Message}";
                    }
                }
                List<UploadRequest> requests = opened.Select(o => new UploadRequest(
                    containerId, UploadName(o.Doc, o.Index), o.Stream, Path: "/",
                    ContentType: o.Doc.Kind == DocumentKind.Text ? "text/plain" : null,
                    Strategy: _config.ChunkingStrategy, IngestedVia: "Eval")).ToList();
                BulkUploadResult result = requests.Count == 0
                    ? new BulkUploadResult(0, 0, Results: [])
                    : await upload.BulkUploadAsync(new BulkUploadRequest(containerId, requests), ct);
                for (int i = 0; i < opened.Count; i++)
                {
                    UploadResult item = result.Results[i];
                    if (item.Success && item.DocumentId is not null)
                        docMap[item.DocumentId] = opened[i].Doc.Id;
                    else
                        uploadErrors[opened[i].Doc.Id] = item.Error ?? "upload rejected";
                }
            }
            finally
            {
                foreach ((_, _, Stream stream) in opened)
                    await stream.DisposeAsync();
            }
            _log.WriteLine($"[{dataset.Name}] uploaded {Math.Min(start + UploadBatchSize, dataset.Corpus.Count)}/{dataset.Corpus.Count}");
        }

        Dictionary<string, DocumentOutcome> settled = await WaitForDocumentsAsync(documents, dataset.Name, containerId, docMap, wait, ct);
        List<DocumentOutcome> outcomes = dataset.Corpus
            .Select(d => uploadErrors.TryGetValue(d.Id, out string? error)
                ? new DocumentOutcome(d.Id, null, error, null, null, null, false, TimeSpan.Zero)
                : settled[d.Id])
            .ToList();
        // Status is authoritative: the ingestion job can mark a document Indexed after the pipeline
        // recorded it as Failed.
        List<string> failed = outcomes
            .Where(o => o.UploadError is not null || o.Stalled || o.Status == "Failed" || o.IngestionStatus?.IsFailed() == true)
            .Select(o => o.DatasetDocId)
            .ToList();

        _datasets[dataset.Name] = (containerId, docMap);

        // Production builds per-container vector indexes in the background a few minutes after a
        // container grows; build them now so searches see what a settled deployment would (#571).
        await using (AsyncServiceScope indexScope = _host.Services.CreateAsyncScope())
            await indexScope.ServiceProvider.GetRequiredService<Connapse.Storage.Vectors.VectorColumnManager>().EnsureIndexesAsync(ct, waitForOthers: true);

        return new IndexReport(dataset.Corpus.Count, failed.Count, failed) { Outcomes = outcomes };
    }

    public async Task<SearchOutcome> SearchAsync(string dataset, EvalQuery query, int k, CancellationToken ct)
    {
        (Guid containerId, Dictionary<string, string> docMap) = _datasets[dataset];
        Stopwatch stopwatch = Stopwatch.StartNew();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(QueryTimeout);
        try
        {
            await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
            IKnowledgeSearch search = scope.ServiceProvider.GetRequiredService<IKnowledgeSearch>();

            int topK = 3 * k;
            SearchResult result = await search.SearchAsync(query.Text, Options(containerId, topK), timeout.Token);
            IReadOnlyList<RankedDoc> ranked = HitCollapser.Collapse(result.Hits, docMap, k);
            if (ranked.Count < k && result.Hits.Count >= topK)
            {
                result = await search.SearchAsync(query.Text, Options(containerId, 10 * k), timeout.Token);
                ranked = HitCollapser.Collapse(result.Hits, docMap, k);
            }
            TimeSpan elapsed = stopwatch.Elapsed;
            // A reranker that can't be reached, times out or is behind an open circuit hands the
            // original order back; scored as reranked, that would mislabel the run.
            if (!string.Equals(scope.ServiceProvider.GetRequiredService<IOptionsMonitor<SearchSettings>>().CurrentValue.Reranker,
                    "None", StringComparison.OrdinalIgnoreCase)
                && result.Hits.Count > 0 && !result.Hits.Any(h => h.Metadata.ContainsKey("reranker")))
                return new SearchOutcome([], new Trace(elapsed, NoStages),
                    "the reranker did not run (unreachable, timed out or circuit open)");
            CandidateCapture? candidates = null;
            if (_config.CaptureCandidates is int pool)
            {
                // A capture that fails or times out leaves the query's measured ranking alone; replay skips it.
                using CancellationTokenSource captureTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                captureTimeout.CancelAfter(QueryTimeout);
                try
                {
                    candidates = await CaptureAsync(scope.ServiceProvider, query.Text, containerId, docMap, pool, captureTimeout.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    candidates = null;
                }
            }
            List<RetrievedPassage> passages = result.Hits
                .Where(h => docMap.ContainsKey(h.DocumentId))
                .Take(k)
                .Select(h => new RetrievedPassage(docMap[h.DocumentId], h.ChunkId, h.Content))
                .ToList();
            return new SearchOutcome(ranked, new Trace(elapsed, NoStages), null) { Candidates = candidates, Passages = passages };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SearchOutcome([], new Trace(stopwatch.Elapsed, NoStages),
                $"Timed out after {QueryTimeout.TotalSeconds:F0} s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SearchOutcome([], new Trace(stopwatch.Elapsed, NoStages), ex.Message);
        }
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    /// <summary>
    /// The pools hybrid search fuses, taken the way <c>HybridSearchService</c> takes them: each side's
    /// own top chunks, then each side's score for the other side's candidates.
    /// </summary>
    private static async Task<CandidateCapture> CaptureAsync(
        IServiceProvider services, string query, Guid containerId, Dictionary<string, string> docMap, int pool,
        CancellationToken ct)
    {
        VectorSearchService vector = services.GetRequiredService<VectorSearchService>();
        KeywordSearchService keyword = services.GetRequiredService<KeywordSearchService>();
        SearchOptions options = new(TopK: pool, ContainerId: containerId.ToString(), Mode: SearchMode.Hybrid);

        QueryEmbedding embedding = await vector.EmbedQueryAsync(query, options, ct);
        List<SearchHit> vectorHits = await vector.SearchAsync(query, embedding, options, SearchScopes.Unrestricted, ct);
        List<SearchHit> keywordHits = await keyword.SearchAsync(query, options, SearchScopes.Unrestricted, ct);

        Dictionary<string, float> vectorScores = vectorHits.ToDictionary(h => h.ChunkId, h => h.Score);
        Dictionary<string, float> keywordScores = keywordHits.ToDictionary(h => h.ChunkId, h => h.Score);
        List<string> needVector = keywordHits.Select(h => h.ChunkId).Where(id => !vectorScores.ContainsKey(id)).ToList();
        List<string> needKeyword = vectorHits.Select(h => h.ChunkId).Where(id => !keywordScores.ContainsKey(id)).ToList();
        if (needVector.Count > 0)
            foreach (var (id, score) in await vector.ScoreChunksAsync(embedding, needVector, ct))
                vectorScores[id] = score;
        if (needKeyword.Count > 0)
            foreach (var (id, score) in await keyword.ScoreChunksAsync(query, needKeyword, ct))
                keywordScores[id] = score;

        Candidate Describe(SearchHit hit) => new(
            hit.ChunkId,
            docMap.GetValueOrDefault(hit.DocumentId, ""),
            vectorScores.TryGetValue(hit.ChunkId, out float v) ? v : null,
            keywordScores.TryGetValue(hit.ChunkId, out float k) ? k : null);

        return new CandidateCapture(vectorHits.Select(Describe).ToList(), keywordHits.Select(Describe).ToList());
    }

    private SearchOptions Options(Guid containerId, int topK) =>
        new(TopK: topK, ContainerId: containerId.ToString(), Mode: _config.SearchMode);

    private static Stream OpenContent(EvalDocument doc) => doc.Kind == DocumentKind.File
        ? File.OpenRead(doc.FilePath ?? throw new InvalidDataException($"File document '{doc.Id}' has no FilePath."))
        : new MemoryStream(Encoding.UTF8.GetBytes(Compose(doc)));

    // The extension is kept as the dataset wrote it; parser selection lowercases it, as for a user upload.
    private static string UploadName(EvalDocument doc, int index) =>
        doc.Kind == DocumentKind.File ? $"{index:D7}{Path.GetExtension(doc.FilePath)}" : $"{index:D7}.txt";

    private static string Compose(EvalDocument doc) =>
        string.IsNullOrWhiteSpace(doc.Title) ? doc.Text ?? "" : $"{doc.Title}\n\n{doc.Text}";

    /// <summary>
    /// Polls the container until every uploaded document settles: its status is Ready or Failed and its
    /// ingestion state has left Pending (the job writes the state just after the pipeline returns).
    /// With <see cref="IngestionWait.RecordStalls"/>, a document Processing for longer than
    /// <see cref="DocumentTimeout"/> is recorded as stalled, and when nothing has settled for
    /// <see cref="StallTimeout"/> every unsettled document is.
    /// </summary>
    private async Task<Dictionary<string, DocumentOutcome>> WaitForDocumentsAsync(
        IDocumentStore documents, string dataset, Guid containerId, Dictionary<string, string> docMap,
        IngestionWait wait, CancellationToken ct)
    {
        Dictionary<string, DocumentOutcome> settled = new(StringComparer.Ordinal);
        Dictionary<string, DateTime> processingSince = new(StringComparer.Ordinal);
        Dictionary<string, Document> latest = new(StringComparer.Ordinal);
        DateTime started = DateTime.UtcNow;
        DateTime lastProgress = started;
        int lastSettled = -1;

        while (true)
        {
            for (int skip = 0; ; skip += 500)
            {
                IReadOnlyList<Document> page = await documents.ListAsync(containerId, null, skip, 500, ct);
                foreach (Document d in page)
                    if (docMap.ContainsKey(d.Id))
                        latest[d.Id] = d;
                if (page.Count < 500)
                    break;
            }

            DateTime now = DateTime.UtcNow;
            // Ready and failed are final: a document waiting on a retry is Queued, not Failed.
            foreach ((string connapseId, Document d) in latest)
            {
                string datasetId = docMap[connapseId];
                if (settled.ContainsKey(datasetId))
                    continue;
                if (d.Status == DocumentStatus.Ready || d.Status.IsFailed())
                {
                    settled[datasetId] = Outcome(datasetId, d, false, now - started);
                    continue;
                }
                if (d.Status == DocumentStatus.Processing)
                    processingSince.TryAdd(connapseId, now);
                if (wait == IngestionWait.RecordStalls && processingSince.TryGetValue(connapseId, out DateTime since)
                    && now - since > DocumentTimeout)
                    settled[datasetId] = Outcome(datasetId, d, true, now - started);
            }

            if (settled.Count >= docMap.Count)
                return settled;
            if (settled.Count != lastSettled)
            {
                lastSettled = settled.Count;
                lastProgress = now;
                _log.WriteLine($"[{dataset}] ingested {settled.Count}/{docMap.Count}");
            }
            else if (now - lastProgress > StallTimeout)
            {
                if (wait == IngestionWait.ThrowOnStall)
                    throw new TimeoutException(
                        $"[{dataset}] ingestion stalled at {settled.Count}/{docMap.Count} documents for {StallTimeout.TotalMinutes:F0} minutes.");
                foreach ((string connapseId, string datasetId) in docMap)
                    if (!settled.ContainsKey(datasetId))
                        settled[datasetId] = latest.TryGetValue(connapseId, out Document? d)
                            ? Outcome(datasetId, d, true, now - started)
                            : new DocumentOutcome(datasetId, connapseId, null, null, null, null, true, now - started);
                return settled;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private static DocumentOutcome Outcome(string datasetId, Document d, bool stalled, TimeSpan elapsed) =>
        new(datasetId, d.Id, null, d.Metadata.GetValueOrDefault("Status"), d.Metadata.GetValueOrDefault("ErrorMessage"),
            d.Status, stalled, elapsed);
}
