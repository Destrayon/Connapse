using AIKnowledge.Core;
using AIKnowledge.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AIKnowledge.Search.Vector;

/// <summary>
/// Semantic vector search service.
/// Embeds queries and searches the vector store using cosine similarity.
/// </summary>
public class VectorSearchService
{
    private readonly IVectorStore _vectorStore;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly ILogger<VectorSearchService> _logger;

    public VectorSearchService(
        IVectorStore vectorStore,
        IEmbeddingProvider embeddingProvider,
        ILogger<VectorSearchService> logger)
    {
        _vectorStore = vectorStore;
        _embeddingProvider = embeddingProvider;
        _logger = logger;
    }

    /// <summary>
    /// Performs semantic search by embedding the query and searching the vector store.
    /// </summary>
    public async Task<List<SearchHit>> SearchAsync(
        string query,
        SearchOptions options,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _logger.LogWarning("Empty query provided to vector search");
            return [];
        }

        // Embed the query
        var queryVector = await _embeddingProvider.EmbedAsync(query, ct);

        // Build filters for vector store
        var filters = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(options.CollectionId))
        {
            filters["collectionId"] = options.CollectionId;
        }

        // Merge any additional filters from options
        if (options.Filters != null)
        {
            foreach (var (key, value) in options.Filters)
            {
                filters[key] = value;
            }
        }

        // Search the vector store
        var results = await _vectorStore.SearchAsync(
            queryVector,
            options.TopK,
            filters.Count > 0 ? filters : null,
            ct);

        // Convert VectorSearchResult to SearchHit
        var hits = results
            .Where(r => r.Score >= options.MinScore)
            .Select(r => new SearchHit(
                ChunkId: r.Id,
                DocumentId: r.Metadata.GetValueOrDefault("documentId", ""),
                Content: r.Metadata.GetValueOrDefault("content", ""),
                Score: r.Score,
                Metadata: r.Metadata))
            .ToList();

        _logger.LogInformation(
            "Vector search for query '{Query}' returned {Count} results (topK={TopK}, minScore={MinScore})",
            query,
            hits.Count,
            options.TopK,
            options.MinScore);

        return hits;
    }
}
