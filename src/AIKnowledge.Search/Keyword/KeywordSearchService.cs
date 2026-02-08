using AIKnowledge.Core;
using AIKnowledge.Storage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AIKnowledge.Search.Keyword;

/// <summary>
/// Full-text keyword search service using PostgreSQL FTS.
/// Uses tsvector and tsquery for ranked text search.
/// </summary>
public class KeywordSearchService
{
    private readonly KnowledgeDbContext _context;
    private readonly ILogger<KeywordSearchService> _logger;

    public KeywordSearchService(
        KnowledgeDbContext context,
        ILogger<KeywordSearchService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Performs full-text search using PostgreSQL FTS.
    /// </summary>
    public async Task<List<SearchHit>> SearchAsync(
        string query,
        SearchOptions options,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _logger.LogWarning("Empty query provided to keyword search");
            return [];
        }

        // Sanitize query for tsquery (replace special characters, handle phrases)
        var tsQuery = SanitizeQuery(query);

        // Build WHERE clause for filters
        // Use {N} format consistently for EF Core's SqlQueryRaw parameterization
        var whereClauses = new List<string> { "1=1" };
        var parameters = new List<object> { tsQuery }; // {0} = tsQuery

        if (!string.IsNullOrEmpty(options.ContainerId))
        {
            var idx = parameters.Count;
            whereClauses.Add($"d.container_id = {{{idx}}}");
            parameters.Add(Guid.Parse(options.ContainerId));
        }

        if (options.Filters != null && options.Filters.TryGetValue("documentId", out var documentId))
        {
            if (Guid.TryParse(documentId, out var docId))
            {
                var idx = parameters.Count;
                whereClauses.Add($"c.document_id = {{{idx}}}");
                parameters.Add(docId);
            }
        }

        if (options.Filters != null && options.Filters.TryGetValue("pathPrefix", out var pathPrefix))
        {
            if (!string.IsNullOrWhiteSpace(pathPrefix))
            {
                var idx = parameters.Count;
                whereClauses.Add($"d.path LIKE {{{idx}}}");
                parameters.Add(pathPrefix + "%");
            }
        }

        var topKIdx = parameters.Count;
        parameters.Add(options.TopK);

        var whereClause = string.Join(" AND ", whereClauses);

        // Use ts_rank for relevance scoring
        // ts_rank returns a float4 score based on TF-IDF-like ranking
        // Note: all parameters use {N} format for SqlQueryRaw
        var sql = @$"
            SELECT
                c.id as ChunkId,
                c.document_id as DocumentId,
                c.content as Content,
                c.chunk_index as ChunkIndex,
                ts_rank(c.search_vector, plainto_tsquery('english', {{{0}}})) as Rank,
                d.file_name as FileName,
                d.content_type as ContentType,
                d.container_id as ContainerId
            FROM chunks c
            INNER JOIN documents d ON c.document_id = d.id
            WHERE {whereClause}
              AND c.search_vector @@ plainto_tsquery('english', {{{0}}})
            ORDER BY Rank DESC
            LIMIT {{{topKIdx}}}";

        var results = await _context.Database
            .SqlQueryRaw<KeywordSearchRow>(sql, parameters.ToArray())
            .ToListAsync(ct);

        // Normalize rank scores to 0-1 range
        // ts_rank typically returns values between 0 and ~1, but can go higher
        var maxRank = results.Count > 0 ? results.Max(r => r.Rank) : 1.0f;
        var minRank = results.Count > 0 ? results.Min(r => r.Rank) : 0.0f;
        var rankRange = maxRank - minRank;

        var hits = results
            .Select(r =>
            {
                // Normalize to 0-1, handling edge case where all ranks are identical
                var normalizedScore = rankRange > 0
                    ? (r.Rank - minRank) / rankRange
                    : 1.0f;

                return new SearchHit(
                    ChunkId: r.ChunkId.ToString(),
                    DocumentId: r.DocumentId.ToString(),
                    Content: r.Content,
                    Score: normalizedScore,
                    Metadata: new Dictionary<string, string>
                    {
                        { "documentId", r.DocumentId.ToString() },
                        { "fileName", r.FileName },
                        { "contentType", r.ContentType ?? "" },
                        { "containerId", r.ContainerId.ToString() },
                        { "chunkIndex", r.ChunkIndex.ToString() },
                        { "rawRank", r.Rank.ToString("F4") }
                    });
            })
            .Where(h => h.Score >= options.MinScore)
            .ToList();

        _logger.LogInformation(
            "Keyword search for query '{Query}' returned {Count} results (topK={TopK}, minScore={MinScore})",
            query,
            hits.Count,
            options.TopK,
            options.MinScore);

        return hits;
    }

    /// <summary>
    /// Sanitizes user query for safe use with tsquery.
    /// Removes special characters that could break the query.
    /// </summary>
    private static string SanitizeQuery(string query)
    {
        // Remove characters that have special meaning in tsquery
        // Keep alphanumeric, spaces, and common punctuation
        var sanitized = new string(query
            .Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c == '-' || c == '_')
            .ToArray());

        // Collapse multiple spaces
        return string.Join(" ", sanitized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // DTO for raw SQL query result
    private record KeywordSearchRow(
        Guid ChunkId,
        Guid DocumentId,
        string Content,
        int ChunkIndex,
        float Rank,
        string FileName,
        string? ContentType,
        Guid ContainerId);
}
