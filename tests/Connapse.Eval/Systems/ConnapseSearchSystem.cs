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
            ["chunking.strategy"] = chunking.Strategy,
            ["chunking.maxChunkSize"] = chunking.MaxChunkSize.ToString(CultureInfo.InvariantCulture),
            ["chunking.overlap"] = chunking.Overlap.ToString(CultureInfo.InvariantCulture),
        };
    }

    public async Task<IndexReport> IndexAsync(EvalDataset dataset, CancellationToken ct)
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
        List<string> failed = [];

        for (int start = 0; start < dataset.Corpus.Count; start += UploadBatchSize)
        {
            List<EvalDocument> batch = dataset.Corpus.Skip(start).Take(UploadBatchSize).ToList();
            List<MemoryStream> streams = batch.Select(d => new MemoryStream(Encoding.UTF8.GetBytes(Compose(d)))).ToList();
            try
            {
                List<UploadRequest> requests = batch.Select((d, i) => new UploadRequest(
                    containerId, $"{start + i:D7}.txt", streams[i], Path: "/", ContentType: "text/plain",
                    IngestedVia: "Eval")).ToList();
                BulkUploadResult result = await upload.BulkUploadAsync(new BulkUploadRequest(containerId, requests), ct);
                for (int i = 0; i < batch.Count; i++)
                {
                    UploadResult item = result.Results[i];
                    if (item.Success && item.DocumentId is not null)
                        docMap[item.DocumentId] = batch[i].Id;
                    else
                        failed.Add(batch[i].Id);
                }
            }
            finally
            {
                foreach (MemoryStream stream in streams)
                    await stream.DisposeAsync();
            }
            _log.WriteLine($"[{dataset.Name}] uploaded {Math.Min(start + UploadBatchSize, dataset.Corpus.Count)}/{dataset.Corpus.Count}");
        }

        // ContainerStats.FailedCount is authoritative; the listed IDs can fall short of it if a
        // document's IngestionState lags its status, so the count decides validity.
        int uploadFailures = failed.Count;
        int ingestionFailures = await WaitForIngestionAsync(documents, dataset.Name, containerId, docMap.Count, ct);
        if (ingestionFailures > 0)
            failed.AddRange(await FailedDatasetIdsAsync(documents, containerId, docMap, ct));

        _datasets[dataset.Name] = (containerId, docMap);
        return new IndexReport(dataset.Corpus.Count, Math.Max(failed.Count, uploadFailures + ingestionFailures), failed);
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

    private static string Compose(EvalDocument doc) =>
        string.IsNullOrWhiteSpace(doc.Title) ? doc.Text ?? "" : $"{doc.Title}\n\n{doc.Text}";

    private async Task<int> WaitForIngestionAsync(
        IDocumentStore documents, string dataset, Guid containerId, int expected, CancellationToken ct)
    {
        int lastDone = -1;
        DateTime lastProgress = DateTime.UtcNow;
        while (true)
        {
            ContainerStats stats = await documents.GetContainerStatsAsync(containerId, ct);
            int done = stats.ReadyCount + stats.FailedCount;
            if (done >= expected)
                return stats.FailedCount;
            if (done != lastDone)
            {
                lastDone = done;
                lastProgress = DateTime.UtcNow;
                _log.WriteLine($"[{dataset}] ingested {done}/{expected}");
            }
            else if (DateTime.UtcNow - lastProgress > StallTimeout)
            {
                throw new TimeoutException(
                    $"[{dataset}] ingestion stalled at {done}/{expected} documents for {StallTimeout.TotalMinutes:F0} minutes.");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private static async Task<IEnumerable<string>> FailedDatasetIdsAsync(
        IDocumentStore documents, Guid containerId, Dictionary<string, string> docMap, CancellationToken ct)
    {
        List<string> failed = [];
        for (int skip = 0; ; skip += 500)
        {
            IReadOnlyList<Document> page = await documents.ListAsync(containerId, null, skip, 500, ct);
            failed.AddRange(page
                .Where(d => d.IngestionState == IngestionState.Failed && docMap.ContainsKey(d.Id))
                .Select(d => docMap[d.Id]));
            if (page.Count < 500)
                return failed;
        }
    }
}
