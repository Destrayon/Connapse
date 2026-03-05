using Connapse.Core;
using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Connapse.Search.Keyword;

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

        // Build WHERE clause for filters
        var whereClauses = new List<string> { "1=1" };
        var parameters = new List<object> { query }; // {0} = raw query string

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

        // websearch_to_tsquery handles user input natively: quoted phrases, negation, OR.
        // Query both 'simple' (exact tokens) and 'english' (stemmed) configs so that
        // technical terms like "README" match exactly while "running" still matches "run".
        // ts_rank_cd uses cover density ranking; normalization flag 32 = rank/(rank+1) for 0-1 range.
        var sql = @$"
            SELECT
                c.id as ChunkId,
                c.document_id as DocumentId,
                c.content as Content,
                c.chunk_index as ChunkIndex,
                ts_rank_cd(c.search_vector,
                    websearch_to_tsquery('simple', {{{0}}}) || websearch_to_tsquery('english', {{{0}}}),
                    32) as Rank,
                d.file_name as FileName,
                d.content_type as ContentType,
                d.container_id as ContainerId,
                d.path as Path
            FROM chunks c
            INNER JOIN documents d ON c.document_id = d.id
            WHERE {whereClause}
              AND c.search_vector @@ (websearch_to_tsquery('simple', {{{0}}}) || websearch_to_tsquery('english', {{{0}}}))
            ORDER BY Rank DESC
            LIMIT {{{topKIdx}}}";

        var results = await _context.Database
            .SqlQueryRaw<KeywordSearchRow>(sql, parameters.ToArray())
            .ToListAsync(ct);

        var hits = results
            .Select(r => new SearchHit(
                ChunkId: r.ChunkId.ToString(),
                DocumentId: r.DocumentId.ToString(),
                Content: r.Content,
                Score: r.Rank,
                Metadata: new Dictionary<string, string>
                {
                    { "documentId", r.DocumentId.ToString() },
                    { "fileName", r.FileName },
                    { "contentType", r.ContentType ?? "" },
                    { "containerId", r.ContainerId.ToString() },
                    { "chunkIndex", r.ChunkIndex.ToString() },
                    { "rawRank", r.Rank.ToString("F6") },
                    { "path", r.Path }
                }))
            .ToList();

        _logger.LogInformation(
            "Keyword search for query '{Query}' returned {Count} results (topK={TopK})",
            query,
            hits.Count,
            options.TopK);

        return hits;
    }

    private record KeywordSearchRow(
        Guid ChunkId,
        Guid DocumentId,
        string Content,
        int ChunkIndex,
        float Rank,
        string FileName,
        string? ContentType,
        Guid ContainerId,
        string Path);
}
