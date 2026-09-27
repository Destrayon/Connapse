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
        string tsQuery = TsQuerySql("{0}", "{1}");

        var sql = @$"
            SELECT
                c.id as ChunkId,
                c.document_id as DocumentId,
                c.content as Content,
                c.chunk_index as ChunkIndex,
                {RankSql("c.search_vector", tsQuery, "{0}")} as Rank,
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
        var parsed = KeywordQuery.Parse(query ?? "");
        if (parsed.Clauses.Count == 0 || ids.Length == 0)
            return new Dictionary<string, float>();

        string sql = @$"
            SELECT
                c.id as ChunkId,
                COALESCE({RankSql("c.search_vector", TsQuerySql("{0}", "{1}"), "{0}")}, 0) as Rank
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
    /// back exactly.
    /// <para>
    /// Stop words: a query of nothing but stop words ("the who") has no english lexemes, so its
    /// clauses are matched on the simple config instead, still any-of and still phrases. When other
    /// words are present, stop words are dropped, as Lucene's analyzers do. An exclusion is a hard
    /// filter, so each one falls back on its own: "-the" or "-who" still excludes.
    /// </para>
    /// </summary>
    internal static string TsQuerySql(string clausesParam, string exclusionsParam) => $"""
        (SELECT CASE WHEN n.q IS NULL THEN p.q ELSE p.q && !!n.q END
         FROM (SELECT COALESCE({AnyClauseSql(clausesParam, "english")}, {AnyClauseSql(clausesParam, "simple")})) p(q),
              (SELECT string_agg('(' || e::text || ')', ' | ') FILTER (WHERE numnode(e) > 0)::tsquery
               FROM unnest({exclusionsParam}::text[]) t,
                    LATERAL (SELECT CASE WHEN numnode(phraseto_tsquery('english', t)) > 0
                                         THEN phraseto_tsquery('english', t)
                                         ELSE phraseto_tsquery('simple', t) END) x(e)) n(q))
        """;

    /// <summary>
    /// A tsquery matching any of the clauses in a text[] parameter, each a phrase in the given text
    /// search config; NULL when no clause has a lexeme in it.
    /// </summary>
    internal static string AnyClauseSql(string clausesParam, string config) => $"""
        (SELECT string_agg('(' || e::text || ')', ' | ') FILTER (WHERE numnode(e) > 0)::tsquery
         FROM unnest({clausesParam}::text[]) t, LATERAL phraseto_tsquery('{config}', t) e)
        """;

    /// <summary>
    /// ts_rank, not ts_rank_cd: under an OR query every cover is a single occurrence, so cover density
    /// becomes an unsaturated term count ("cat cat cat cat" beats "cat dog"), where ts_rank saturates
    /// repeats and rewards matching more distinct terms. Weights {D,C,B,A} = {0,0,1,0} score only the
    /// english (B) copy of search_vector; the simple (A) copy would otherwise add weight to the terms
    /// whose stem happens to equal the word. A stop-word-only query matches only the simple copy, so
    /// it is ranked on that instead; otherwise every match would tie at zero. Normalization 1 divides
    /// by log length; 32 maps the rank into 0-1 without changing its order. Neither function has
    /// IDF — that is #545.
    /// </summary>
    internal static string RankSql(string vector, string tsQuery, string clausesParam) => $"""
        (CASE WHEN {AnyClauseSql(clausesParam, "english")} IS NULL
              THEN ts_rank(ARRAY[0, 0, 0, 1]::float4[], {vector}, {tsQuery}, 1|32)
              ELSE ts_rank(ARRAY[0, 0, 1, 0]::float4[], {vector}, {tsQuery}, 1|32) END)
        """;

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
