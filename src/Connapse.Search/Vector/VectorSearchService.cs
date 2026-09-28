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
    /// Embeds the query for every vector space it will search: the configured recipe's, plus each
    /// other recipe of the same model that the container still holds vectors under and that can be
    /// reproduced (the model's published recipe, and the bare model for vectors stored before any
    /// text preparation). Each space gets a query prepared its own way, so a query never meets
    /// vectors made differently, and a reindex in progress hides nothing. A caller's explicit
    /// <c>modelId</c> filter searches that one space, or nothing if its recipe can't be reproduced.
    /// </summary>
    public async Task<QueryEmbedding> EmbedQueryAsync(string query, SearchOptions options, CancellationToken ct = default)
    {
        EmbeddingSettings settings = _embeddingSettings.CurrentValue;
        IReadOnlyList<EmbeddingSettings> recipes = EmbeddingIdentity.ReproducibleRecipes(settings);
        List<string> spaces = [.. recipes.Select(EmbeddingIdentity.For)];
        if (!spaces.Contains(settings.Model))
            spaces.Add(settings.Model);

        if (options.Filters is not null
            && options.Filters.TryGetValue("modelId", out string? requested)
            && !string.IsNullOrWhiteSpace(requested))
        {
            if (!spaces.Contains(requested))
            {
                _logger.LogWarning(
                    "No semantic results for modelId {ModelId}: its recipe can't be reproduced with the configured model",
                    Sanitize(requested));
                return new QueryEmbedding([]);
            }
            spaces = [requested];
        }
        else if (spaces.Count > 1)
        {
            // The current space is always searched; the others only while the container still has vectors there.
            Guid? containerId = Guid.TryParse(options.ContainerId, out Guid id) ? id : null;
            IReadOnlyList<EmbeddingModelInfo> models = await _modelCounts.GetAsync(containerId, _modelDiscovery, ct);
            HashSet<string> stored = models.Where(m => m.VectorCount > 0).Select(m => m.ModelId).ToHashSet(StringComparer.Ordinal);
            spaces = [spaces[0], .. spaces.Skip(1).Where(stored.Contains)];
        }

        List<QuerySpace> embedded = new(spaces.Count);
        foreach (string space in spaces)
        {
            float[] vector;
            if (space == spaces[0] && space == EmbeddingIdentity.For(settings))
            {
                vector = await _embeddingProvider.EmbedAsync(query, EmbeddingInputType.Query, ct);
            }
            else
            {
                // Prepared here rather than by the provider, which only knows the configured recipe.
                EmbeddingSettings? recipe = recipes.FirstOrDefault(r => EmbeddingIdentity.For(r) == space);
                string text = recipe is null ? query : EmbeddingText.Prepare(recipe, [query], EmbeddingInputType.Query)[0];
                vector = await _embeddingProvider.EmbedAsync(text, EmbeddingInputType.Unspecified, ct);
            }
            embedded.Add(new QuerySpace(vector, space));
        }
        return new QueryEmbedding(embedded);
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

        // Each space filters on its own model id: cosine similarity between vectors from different
        // models or recipes is meaningless. A chunk lives in one space at a time, so the union has
        // no duplicates; scores from recipes of one model are close enough to merge during a reindex.
        var results = new List<VectorSearchResult>();
        foreach (QuerySpace space in queryEmbedding.Spaces)
        {
            filters["modelId"] = space.ModelId;
            results.AddRange(await _vectorStore.SearchAsync(
                space.Vector,
                options.TopK,
                new Dictionary<string, string>(filters),
                scopes,
                ct));
        }
        if (queryEmbedding.Spaces.Count > 1)
            results = results.OrderByDescending(r => r.Score).Take(options.TopK).ToList();

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
    public async Task<IReadOnlyDictionary<string, float>> ScoreChunksAsync(
        QueryEmbedding queryEmbedding,
        IReadOnlyCollection<string> chunkIds,
        CancellationToken ct = default)
    {
        if (queryEmbedding.Spaces.Count == 1)
        {
            QuerySpace only = queryEmbedding.Spaces[0];
            return await _vectorStore.ScoreChunksAsync(only.Vector, chunkIds, only.ModelId, ct);
        }

        Dictionary<string, float> scores = new(StringComparer.Ordinal);
        foreach (QuerySpace space in queryEmbedding.Spaces)
            foreach (var (chunkId, score) in await _vectorStore.ScoreChunksAsync(space.Vector, chunkIds, space.ModelId, ct))
                scores.TryAdd(chunkId, score);
        return scores;
    }
}
