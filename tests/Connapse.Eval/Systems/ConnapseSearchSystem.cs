using System.Diagnostics;
using System.Globalization;
using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Eval.Model;
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
    private readonly SystemConfig _config;
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

    public static async Task<ConnapseSearchSystem> StartAsync(
        SystemConfig config, string webContentRoot, EmbeddingDiskCache cache, TextWriter log,
        IEmbeddingProvider? embeddingOverride, CancellationToken ct)
    {
        EvalHost host = await EvalHost.StartAsync(config, webContentRoot, cache, embeddingOverride, ct);
        return new ConnapseSearchSystem(host, config, log, embeddingOverride);
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
            List<Stream> streams = batch.Select(OpenContent).ToList();
            try
            {
                List<UploadRequest> requests = batch.Select((d, i) => new UploadRequest(
                    containerId, UploadName(d, start + i), streams[i], Path: "/",
                    ContentType: d.Kind == DocumentKind.Text ? "text/plain" : null,
                    Strategy: _config.ChunkingStrategy, IngestedVia: "Eval")).ToList();
                BulkUploadResult result = await upload.BulkUploadAsync(new BulkUploadRequest(containerId, requests), ct);
                for (int i = 0; i < batch.Count; i++)
                {
                    UploadResult item = result.Results[i];
                    if (item.Success && item.DocumentId is not null)
                        docMap[item.DocumentId] = batch[i].Id;
                    else
                        uploadErrors[batch[i].Id] = item.Error ?? "upload rejected";
                }
            }
            finally
            {
                foreach (Stream stream in streams)
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
            .Where(o => o.UploadError is not null || o.Stalled || o.Status == "Failed" || o.IngestionState == IngestionState.Failed)
            .Select(o => o.DatasetDocId)
            .ToList();

        _datasets[dataset.Name] = (containerId, docMap);
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
            return new SearchOutcome(ranked, new Trace(stopwatch.Elapsed, NoStages), null);
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
            foreach ((string connapseId, Document d) in latest)
            {
                string datasetId = docMap[connapseId];
                if (settled.ContainsKey(datasetId))
                    continue;
                string? status = d.Metadata.GetValueOrDefault("Status");
                if (status is "Ready" or "Failed" && d.IngestionState != IngestionState.Pending)
                {
                    settled[datasetId] = Outcome(datasetId, d, false, now - started);
                    continue;
                }
                if (status == "Processing")
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
            d.IngestionState, stalled, elapsed);
}
