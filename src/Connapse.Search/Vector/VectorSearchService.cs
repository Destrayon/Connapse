using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private readonly ILogger<VectorSearchService> _logger;

    public VectorSearchService(
        IVectorStore vectorStore,
        IEmbeddingProvider embeddingProvider,
        IOptionsMonitor<EmbeddingSettings> embeddingSettings,
        ILogger<VectorSearchService> logger)
    {
        _vectorStore = vectorStore;
        _embeddingProvider = embeddingProvider;
        _embeddingSettings = embeddingSettings;
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

        // Embed the query
        var queryVector = await EmbedQueryAsync(query, ct);

        return await SearchAsync(query, queryVector, options, scopes, ct);
    }

    public Task<float[]> EmbedQueryAsync(string query, CancellationToken ct = default) =>
        _embeddingProvider.EmbedAsync(query, ct);

    /// <summary>
    /// Searches with a query vector the caller already embedded, so hybrid search can reuse it to
    /// score keyword-only candidates without embedding the query twice.
    /// </summary>
    public async Task<List<SearchHit>> SearchAsync(
        string query,
        float[] queryVector,
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
        if (!filters.ContainsKey("modelId"))
        {
            filters["modelId"] = _embeddingSettings.CurrentValue.Model;
        }

        // Search the vector store
        var results = await _vectorStore.SearchAsync(
            queryVector,
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
    /// Similarity of the named chunks to <paramref name="queryVector"/> under the current embedding
    /// model. Chunks embedded with another model have no comparable vector and are left out.
    /// </summary>
    public Task<IReadOnlyDictionary<string, float>> ScoreChunksAsync(
        float[] queryVector,
        IReadOnlyCollection<string> chunkIds,
        CancellationToken ct = default) =>
        _vectorStore.ScoreChunksAsync(queryVector, chunkIds, _embeddingSettings.CurrentValue.Model, ct);
}
