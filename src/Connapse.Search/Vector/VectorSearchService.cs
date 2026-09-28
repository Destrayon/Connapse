using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Connapse.Storage.Vectors;
using static Connapse.Core.Utilities.LogSanitizer;

namespace Connapse.Search.Vector;

/// <summary>
/// Semantic vector search service.
/// Embeds queries and searches the vector store using cosine similarity.
/// </summary>
public class VectorSearchService
{
    private readonly IVectorStore _vectorStore;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IOptionsMonitor<EmbeddingSettings> _embeddingSettings;
    private readonly VectorModelDiscovery _modelDiscovery;
    private readonly VectorModelCountCache _modelCounts;
    private readonly ILogger<VectorSearchService> _logger;

    public VectorSearchService(
        IVectorStore vectorStore,
        IEmbeddingProvider embeddingProvider,
        IOptionsMonitor<EmbeddingSettings> embeddingSettings,
        VectorModelDiscovery modelDiscovery,
        VectorModelCountCache modelCounts,
        ILogger<VectorSearchService> logger)
    {
        _vectorStore = vectorStore;
        _embeddingProvider = embeddingProvider;
        _embeddingSettings = embeddingSettings;
        _modelDiscovery = modelDiscovery;
        _modelCounts = modelCounts;
        _logger = logger;
    }

    /// <summary>
    /// Performs semantic search by embedding the query and searching the vector store.
    /// </summary>
    /// <param name="scopes">
    /// What the caller may reach. Required rather than optional: a default would make forgetting
    /// it compile, and forgetting it here returns everything to everyone.
    /// </param>
    public async Task<List<SearchHit>> SearchAsync(
        string query,
        SearchOptions options,
        SearchScopes scopes,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _logger.LogWarning("Empty query provided to vector search");
            return [];
        }

        QueryEmbedding embedding = await EmbedQueryAsync(query, options, ct);

        return await SearchAsync(query, embedding, options, scopes, ct);
    }

    /// <summary>
    /// Embeds the query for the vector space it will search. That is the configured model's space
    /// (its query prompt applied), except while a container still holds mostly vectors stored
    /// before the model's prompts applied: then the query goes in as-is and searches those, so a
    /// query never meets vectors made with a different recipe. A reindex moves the container over.
    /// A caller's explicit <c>modelId</c> filter picks the space instead.
    /// </summary>
    public async Task<QueryEmbedding> EmbedQueryAsync(string query, SearchOptions options, CancellationToken ct = default)
    {
        EmbeddingSettings settings = _embeddingSettings.CurrentValue;
        string current = EmbeddingIdentity.For(settings);
        string modelId = current;

        if (options.Filters is not null
            && options.Filters.TryGetValue("modelId", out string? requested)
            && !string.IsNullOrWhiteSpace(requested))
        {
            modelId = requested;
        }
        else if (!string.Equals(current, settings.Model, StringComparison.Ordinal))
        {
            Guid? containerId = Guid.TryParse(options.ContainerId, out Guid id) ? id : null;
            IReadOnlyList<EmbeddingModelInfo> models = await _modelCounts.GetAsync(containerId, _modelDiscovery, ct);
            long Count(string model) => models.Where(m => m.ModelId == model).Sum(m => m.VectorCount);
            if (Count(settings.Model) > Count(current))
            {
                modelId = settings.Model;
                _logger.LogDebug("Searching vectors stored before {Model}'s prompts applied; reindex to move them", settings.Model);
            }
        }

        EmbeddingInputType inputType = modelId == current ? EmbeddingInputType.Query : EmbeddingInputType.Unspecified;
        float[] vector = await _embeddingProvider.EmbedAsync(query, inputType, ct);
        return new QueryEmbedding(vector, modelId);
    }

    /// <summary>
    /// Searches with a query the caller already embedded, so hybrid search can reuse it to
    /// score keyword-only candidates without embedding the query twice.
    /// </summary>
    public async Task<List<SearchHit>> SearchAsync(
        string query,
        QueryEmbedding queryEmbedding,
        SearchOptions options,
        SearchScopes scopes,
        CancellationToken ct = default)
    {
        // Build filters for vector store
        var filters = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(options.ContainerId))
        {
            filters["containerId"] = options.ContainerId;
        }

        // Merge any additional filters from options
        if (options.Filters != null)
        {
            foreach (var (key, value) in options.Filters)
            {
                filters[key] = value;
            }
        }

        // Filter by current embedding model to ensure dimension consistency.
        // Cosine similarity between vectors from different models is meaningless.
        filters["modelId"] = queryEmbedding.ModelId;

        // Search the vector store
        var results = await _vectorStore.SearchAsync(
            queryEmbedding.Vector,
            options.TopK,
            filters.Count > 0 ? filters : null,
            scopes,
            ct);

        // Convert VectorSearchResult to SearchHit (MinScore applied later by HybridSearchService)
        var hits = results
            .Select(r => new SearchHit(
                ChunkId: r.Id,
                DocumentId: r.Metadata.GetValueOrDefault("documentId", ""),
                Content: r.Metadata.GetValueOrDefault("content", ""),
                Score: r.Score,
                Metadata: r.Metadata))
            .ToList();

        _logger.LogInformation(
            "Vector search for query '{Query}' returned {Count} results (topK={TopK}, minScore={MinScore})",
            Sanitize(query),
            hits.Count,
            options.TopK,
            options.MinScore);

        return hits;
    }

    /// <summary>
    /// Similarity of the named chunks to the query, in the vector space the query was embedded for,
    /// so both halves of a hybrid search compare against one model. Chunks with no vector there are
    /// left out.
    /// </summary>
    public Task<IReadOnlyDictionary<string, float>> ScoreChunksAsync(
        QueryEmbedding queryEmbedding,
        IReadOnlyCollection<string> chunkIds,
        CancellationToken ct = default) =>
        _vectorStore.ScoreChunksAsync(queryEmbedding.Vector, chunkIds, queryEmbedding.ModelId, ct);
}
