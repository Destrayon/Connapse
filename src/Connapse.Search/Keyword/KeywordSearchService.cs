using Connapse.Core;
using Connapse.Storage.Data;
using Connapse.Storage.Keyword;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Connapse.Core.Utilities.LogSanitizer;

namespace Connapse.Search.Keyword;

public class KeywordSearchService
{
    private readonly KnowledgeDbContext _context;
    private readonly ILogger<KeywordSearchService> _logger;
    private readonly IOptionsMonitor<SearchSettings>? _settings;
    private readonly Bm25IndexManager? _bm25;

    public KeywordSearchService(
        KnowledgeDbContext context,
        ILogger<KeywordSearchService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public KeywordSearchService(
        KnowledgeDbContext context,
        ILogger<KeywordSearchService> logger,
        IOptionsMonitor<SearchSettings> settings,
        Bm25IndexManager bm25)
        : this(context, logger)
    {
        _settings = settings;
        _bm25 = bm25;
    }

    /// <summary>
    /// The owner's BM25 index when BM25 ranking is configured and available; null means ts_rank.
    /// </summary>
    private async Task<string?> Bm25IndexAsync(Guid ownerId, CancellationToken ct) =>
        _bm25 is not null
        && string.Equals(_settings?.CurrentValue.KeywordRanker, "Bm25", StringComparison.OrdinalIgnoreCase)
            ? await _bm25.GetIndexNameAsync(ownerId, ct)
            : null;

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

        var parsed = KeywordQuery.Parse(query);
        if (parsed.Clauses.Count == 0)
        {
            // Nothing to match, only things to exclude: like Lucene, that matches nothing.
            return [];
        }

        // Build WHERE clause for filters
        // A source whose remote revoked access (a public repository gone private) is left out,
        // whatever else the caller asked for.
        var whereClauses = new List<string> { "1=1", "NOT EXISTS (SELECT 1 FROM sources s WHERE s.id = d.source_id AND s.access_revoked_at IS NOT NULL)" };
        // {0} = clauses, {1} = exclusions
        var parameters = new List<object> { parsed.Clauses.ToArray(), parsed.Exclusions.ToArray() };

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

        string? bm25Index = string.IsNullOrEmpty(options.ContainerId)
            ? null
            : await Bm25IndexAsync(Guid.Parse(options.ContainerId), ct);
        if (bm25Index is not null)
        {
            List<SearchHit> bm25Hits = ToHits(await Bm25SearchAsync(
                whereClause, parameters, topKIdx, parsed, bm25Index, Guid.Parse(options.ContainerId!), ct));

            // A query of nothing but stop words has no BM25 terms; ts_rank's exact-token fallback
            // still finds "the who".
            if (bm25Hits.Count > 0)
            {
                LogResults(query, bm25Hits.Count, options.TopK);
                return bm25Hits;
            }
        }

        string tsQuery = TsQuerySql("{0}", "{1}");

        var sql = @$"
            SELECT
                c.id as ChunkId,
                c.document_id as DocumentId,
                c.content as Content,
                c.chunk_index as ChunkIndex,
                {RankSql("c.search_vector", tsQuery)} as Rank,
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

        var hits = ToHits(results);
        LogResults(query, hits.Count, options.TopK);
        return hits;
    }

    /// <summary>
    /// Ranks with pg_textsearch BM25 over the owner's partial index. The index scan orders by the
    /// negative BM25 score (lower is better) and every chunk it covers gets a score, 0 when no term
    /// matches, so the "< 0" filter is what makes it a match. Exclusions reuse the tsquery path.
    /// </summary>
    private async Task<List<KeywordSearchRow>> Bm25SearchAsync(
        string whereClause, List<object> parameters, int topKIdx, KeywordQuery parsed,
        string indexName, Guid ownerId, CancellationToken ct)
    {
        List<object> bm25Parameters = [.. parameters, string.Join(' ', parsed.Clauses), indexName, ownerId];
        int textIdx = parameters.Count, indexIdx = textIdx + 1, ownerIdx = textIdx + 2;
        string score = $"(c.content <@> to_bm25query({{{textIdx}}}, {{{indexIdx}}}))";
        string exclusions = parsed.Exclusions.Count == 0
            ? ""
            : $"AND NOT COALESCE(c.search_vector @@ {AnyOfSql("{1}")}, false)";

        // c.owner_id (not only d.owner_id) so the planner can prove the partial index applies.
        string sql = @$"
            SELECT
                c.id as ChunkId,
                c.document_id as DocumentId,
                c.content as Content,
                c.chunk_index as ChunkIndex,
                (-{score})::real as Rank,
                d.file_name as FileName,
                d.content_type as ContentType,
                d.owner_id as ContainerId,
                d.path as Path
            FROM chunks c
            INNER JOIN documents d ON c.document_id = d.id
            WHERE {whereClause}
              AND c.owner_id = {{{ownerIdx}}}
              AND {score} < 0
              {exclusions}
            ORDER BY {score}
            LIMIT {{{topKIdx}}}";

        return await _context.Database
            .SqlQueryRaw<KeywordSearchRow>(sql, bm25Parameters.ToArray())
            .ToListAsync(ct);
    }

    private static List<SearchHit> ToHits(List<KeywordSearchRow> results) =>
        results
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

    private void LogResults(string query, int count, int topK) =>
        _logger.LogInformation(
            "Keyword search for query '{Query}' returned {Count} results (topK={TopK})",
            Sanitize(query),
            count,
            topK);

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
        var parsed = KeywordQuery.Parse(query ?? "");
        if (parsed.Clauses.Count == 0 || ids.Length == 0)
            return new Dictionary<string, float>();

        // BM25 scores only mean something against one index's statistics, so a pool spanning owners
        // (not something a container-scoped search produces) is scored with ts_rank instead.
        List<Guid> owners = await _context.Database
            .SqlQueryRaw<Guid>("SELECT DISTINCT owner_id AS \"Value\" FROM chunks WHERE id = ANY({0})", ids)
            .ToListAsync(ct);
        string? bm25Index = owners.Count == 1 ? await Bm25IndexAsync(owners[0], ct) : null;
        if (bm25Index is not null)
        {
            string exclusions = parsed.Exclusions.Count == 0
                ? "false"
                : $"COALESCE(c.search_vector @@ {AnyOfSql("{1}")}, false)";
            string bm25Sql = @$"
                SELECT
                    c.id as ChunkId,
                    CASE WHEN {exclusions} THEN 0
                         ELSE (-(c.content <@> to_bm25query({{3}}, {{4}})))::real END as Rank
                FROM chunks c
                WHERE c.id = ANY({{2}})";

            List<ChunkRankRow> bm25Rows = await _context.Database
                .SqlQueryRaw<ChunkRankRow>(bm25Sql,
                    parsed.Clauses.ToArray(), parsed.Exclusions.ToArray(), ids, string.Join(' ', parsed.Clauses), bm25Index)
                .ToListAsync(ct);
            return bm25Rows.ToDictionary(r => r.ChunkId.ToString(), r => r.Rank);
        }

        string sql = @$"
            SELECT
                c.id as ChunkId,
                COALESCE({RankSql("c.search_vector", TsQuerySql("{0}", "{1}"))}, 0) as Rank
            FROM chunks c
            WHERE c.id = ANY({{2}})";

        List<ChunkRankRow> rows = await _context.Database
            .SqlQueryRaw<ChunkRankRow>(sql, parsed.Clauses.ToArray(), parsed.Exclusions.ToArray(), ids)
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.ChunkId.ToString(), r => r.Rank);
    }

    /// <summary>
    /// The tsquery for a parsed <see cref="KeywordQuery"/>, bound through two text[] parameters.
    /// A chunk matches when it contains <em>any</em> clause and no exclusion; requiring every term
    /// matched almost nothing for natural-language questions (#544). Each clause goes through
    /// phraseto_tsquery, so a quoted phrase stays a phrase and a lone word is just that word, stemmed
    /// and stop-word free. The clauses are joined through tsquery's own text form, which it parses
    /// back exactly. A query of nothing but stop words ("the who") has no english lexemes and falls
    /// back to all of its exact tokens.
    /// </summary>
    internal static string TsQuerySql(string clausesParam, string exclusionsParam) => $"""
        (SELECT CASE WHEN n.q IS NULL THEN p.q ELSE p.q && !!n.q END
         FROM (SELECT COALESCE(
                   string_agg('(' || e::text || ')', ' | ') FILTER (WHERE numnode(e) > 0)::tsquery,
                   plainto_tsquery('simple', array_to_string({clausesParam}::text[], ' ')))
               FROM unnest({clausesParam}::text[]) t, LATERAL phraseto_tsquery('english', t) e) p(q),
              (SELECT {AnyOfSql(exclusionsParam)}) n(q))
        """;

    /// <summary>
    /// A tsquery matching any of the phrases in a text[] parameter; NULL when none has a lexeme.
    /// </summary>
    internal static string AnyOfSql(string param) => $"""
        (SELECT string_agg('(' || e::text || ')', ' | ') FILTER (WHERE numnode(e) > 0)::tsquery
         FROM unnest({param}::text[]) t, LATERAL phraseto_tsquery('english', t) e)
        """;

    /// <summary>
    /// ts_rank, not ts_rank_cd: under an OR query every cover is a single occurrence, so cover density
    /// becomes an unsaturated term count ("cat cat cat cat" beats "cat dog"), where ts_rank saturates
    /// repeats and rewards matching more distinct terms. Weights {D,C,B,A} = {0,0,1,0} score only the
    /// english (B) copy of search_vector; the simple (A) copy would otherwise add weight to the terms
    /// whose stem happens to equal the word. Normalization 1 divides by log length; 32 maps the rank
    /// into 0-1 without changing its order. Neither function has IDF — that is #545.
    /// </summary>
    internal static string RankSql(string vector, string tsQuery) =>
        $"ts_rank(ARRAY[0, 0, 1, 0]::float4[], {vector}, {tsQuery}, 1|32)";

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
