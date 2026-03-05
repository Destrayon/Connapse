using Connapse.Core;
using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Connapse.Search.Keyword;

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

        // Build an OR-joined tsquery so any matching term produces results.
        // plainto_tsquery uses AND (all terms required), which misses chunks
        // that contain only some of the query terms.
        var tsQuery = BuildOrQuery(query);

        if (string.IsNullOrEmpty(tsQuery))
        {
            _logger.LogWarning("Query '{Query}' produced no searchable terms", query);
            return [];
        }

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

        // Use ts_rank for relevance scoring with to_tsquery (OR-joined terms).
        // Chunks matching more terms rank higher naturally via ts_rank.
        // The tsQuery string is already a valid tsquery expression (e.g. 'term1' | 'term2'),
        // so we use to_tsquery which accepts pre-formatted tsquery syntax.
        var sql = @$"
            SELECT
                c.id as ChunkId,
                c.document_id as DocumentId,
                c.content as Content,
                c.chunk_index as ChunkIndex,
                ts_rank(c.search_vector, to_tsquery('english', {{{0}}})) as Rank,
                d.file_name as FileName,
                d.content_type as ContentType,
                d.container_id as ContainerId
            FROM chunks c
            INNER JOIN documents d ON c.document_id = d.id
            WHERE {whereClause}
              AND c.search_vector @@ to_tsquery('english', {{{0}}})
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
    /// Builds an OR-joined tsquery expression from user input.
    /// Each term is sanitized and joined with '|' (OR) so that chunks
    /// containing any of the query terms are returned. Chunks matching
    /// more terms will rank higher via ts_rank.
    /// </summary>
    internal static string BuildOrQuery(string query)
    {
        // Split on whitespace, sanitize each term, filter empties
        var terms = query
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizeTerm)
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (terms.Count == 0)
            return string.Empty;

        // Join with OR operator for to_tsquery syntax
        return string.Join(" | ", terms);
    }

    /// <summary>
    /// Sanitizes a single term for safe use in a tsquery expression.
    /// Strips characters that are not valid in tsquery lexemes.
    /// </summary>
    internal static string SanitizeTerm(string term)
    {
        // Strip leading/trailing dots and other punctuation that PostgreSQL
        // FTS would discard (e.g. ".NET" -> "NET", "C#" -> "C", "node.js" -> "node.js")
        // Keep internal dots/hyphens as they can form compound lexemes
        var sanitized = new string(term
            .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')
            .ToArray());

        // Trim leading/trailing dots and hyphens (not valid at boundaries in tsquery)
        sanitized = sanitized.Trim('.', '-', '_');

        // Reject if empty or only special chars remain
        if (string.IsNullOrEmpty(sanitized))
            return string.Empty;

        // Escape any single quotes (defense in depth — to_tsquery is parameterized,
        // but the term flows through the 'english' dictionary as a lexeme)
        sanitized = sanitized.Replace("'", "");

        return sanitized;
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
