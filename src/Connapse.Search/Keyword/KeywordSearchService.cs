using Connapse.Core;
using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static Connapse.Core.Utilities.LogSanitizer;

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
            _logger.LogWarning("Empty query provided to keyword search");
            return [];
        }

        // Build WHERE clause for filters
        // A source whose remote revoked access (a public repository gone private) is left out,
        // whatever else the caller asked for.
        var whereClauses = new List<string> { "1=1", "NOT EXISTS (SELECT 1 FROM sources s WHERE s.id = d.source_id AND s.access_revoked_at IS NOT NULL)" };
        var parameters = new List<object> { query }; // {0} = raw query string

        if (!string.IsNullOrEmpty(options.ContainerId))
        {
            var idx = parameters.Count;
            whereClauses.Add($"d.owner_id = {{{idx}}}");
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

        // The same rule as the vector side, and it has to be: a hit reachable through one
        // mode and not the other is a leak through whichever the caller happens to choose.
        if (scopes is { IsUnrestricted: false })
        {
            if (scopes.IsEmpty)
            {
                // No grants (no principal, no access, or the resolver failed) says nothing about
                // documents the cloud has no opinion on. Only cloud-governed documents — those
                // with a resource_uri — are excluded; everything else (uploads, SFTP, ...) still
                // shows. This also means a resolver outage degrades to "you see the non-cloud
                // documents", not "you see nothing".
                whereClauses.Add("d.resource_uri IS NULL");
            }
            else
            {
                // A document with no resource URI has no cloud coordinate to check permissions
                // against — uploads and connectors that never report one (SFTP, filesystem,
                // MinIO) are like this by design, not by accident. Such a document falls back to
                // Connapse's own access control (container/source reachability) instead of cloud
                // grants, so it is admitted unconditionally here. Cloud scope filtering only
                // narrows the subset of documents that do carry a cloud address.
                var ors = new List<string>();
                foreach (GrantMatch match in scopes.Matches)
                {
                    int scopeIdx = parameters.Count;

                    // A grant scoped to one object is exact-matched rather than treated as a
                    // prefix: as a prefix it would also let through a sibling like
                    // "report.pdf.bak", a different object nobody granted.
                    ors.Add(match.IsExact
                        ? $"d.resource_uri = {{{scopeIdx}}}"
                        : $"d.resource_uri LIKE {{{scopeIdx}}} ESCAPE '{SearchScopes.LikeEscape}'");

                    parameters.Add(match.IsExact
                        ? match.Value
                        : SearchScopes.ToLikePattern(match.Value));
                }

                whereClauses.Add($"(d.resource_uri IS NULL OR ({string.Join(" OR ", ors)}))");
            }
        }

        var whereClause = string.Join(" AND ", whereClauses);
        string tsQuery = TsQuerySql(query, "{0}");

        // ts_rank_cd uses cover density ranking; normalization flag 32 = rank/(rank+1) for 0-1 range.
        var sql = @$"
            SELECT
                c.id as ChunkId,
                c.document_id as DocumentId,
                c.content as Content,
                c.chunk_index as ChunkIndex,
                ts_rank_cd(c.search_vector, {tsQuery}, 32) as Rank,
                d.file_name as FileName,
                d.content_type as ContentType,
                d.owner_id as ContainerId,
                d.path as Path
            FROM chunks c
            INNER JOIN documents d ON c.document_id = d.id
            WHERE {whereClause}
              AND c.search_vector @@ {tsQuery}
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
            Sanitize(query),
            hits.Count,
            options.TopK);

        return hits;
    }

    /// <summary>
    /// The keyword rank the named chunks would have had for <paramref name="query"/>, scored the same
    /// way as <see cref="SearchAsync"/>; a chunk that matches no term scores 0. Scores only — no
    /// permission filter — so callers pass chunks a scoped search has already admitted.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, float>> ScoreChunksAsync(
        string query,
        IReadOnlyCollection<string> chunkIds,
        CancellationToken ct = default)
    {
        Guid[] ids = chunkIds
            .Select(id => Guid.TryParse(id, out Guid g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToArray();
        if (string.IsNullOrWhiteSpace(query) || ids.Length == 0)
            return new Dictionary<string, float>();

        string sql = @$"
            SELECT
                c.id as ChunkId,
                ts_rank_cd(c.search_vector, {TsQuerySql(query, "{0}")}, 32) as Rank
            FROM chunks c
            WHERE c.id = ANY({{1}})";

        List<ChunkRankRow> rows = await _context.Database
            .SqlQueryRaw<ChunkRankRow>(sql, query, ids)
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.ChunkId.ToString(), r => r.Rank);
    }

    /// <summary>
    /// The tsquery SQL for <paramref name="query"/>, bound through <paramref name="param"/>.
    /// A plain query matches a chunk containing <em>any</em> of its terms and leaves the ordering to
    /// ts_rank_cd: requiring every term matched almost nothing for natural-language questions (#544).
    /// A query that uses search syntax (quoted phrases, -exclusion, OR) keeps websearch_to_tsquery
    /// semantics, over both 'simple' (exact tokens like "README") and 'english' (stemmed).
    /// </summary>
    internal static string TsQuerySql(string query, string param) =>
        UsesSearchSyntax(query)
            ? $"(websearch_to_tsquery('simple', {param}) || websearch_to_tsquery('english', {param}))"
            // Stemmed and stop-word free, so "the" doesn't match every chunk. A query of nothing but
            // stop words ("the who") has no english lexemes and falls back to all of its exact tokens.
            // plainto_tsquery joins lexemes with " & " and a lexeme never contains a space, so the
            // replace turns every AND into an OR and nothing else.
            : $"""
               (CASE WHEN numnode(plainto_tsquery('english', {param})) = 0
                    THEN plainto_tsquery('simple', {param})
                    ELSE replace(plainto_tsquery('english', {param})::text, ' & ', ' | ')::tsquery END)
               """;

    internal static bool UsesSearchSyntax(string query) =>
        query.Contains('"')
        || query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(w => (w.Length > 1 && w[0] == '-') || w.Equals("or", StringComparison.OrdinalIgnoreCase));

    private record ChunkRankRow(Guid ChunkId, float Rank);

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
