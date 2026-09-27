using Connapse.Core;
using Connapse.Storage.Data;
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
        IOptionsMonitor<SearchSettings> settings)
        : this(context, logger)
    {
        _settings = settings;
    }

    /// <summary>The search settings when BM25 ranking is configured; null means ts_rank.</summary>
    private SearchSettings? Bm25Settings =>
        string.Equals(_settings?.CurrentValue.KeywordRanker, "Bm25", StringComparison.OrdinalIgnoreCase)
            ? _settings!.CurrentValue
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
        string tsQuery = TsQuerySql("{0}", "{1}");

        if (Bm25Settings is { } bm25 && !string.IsNullOrEmpty(options.ContainerId))
        {
            List<KeywordSearchRow> bm25Rows = await Bm25SearchAsync(
                whereClause, tsQuery, parameters, options.TopK, Guid.Parse(options.ContainerId),
                parsed, bm25, ct);

            // A query of nothing but stop words has no BM25 terms; ts_rank's exact-token fallback
            // below still finds "the who".
            if (bm25Rows.Count > 0)
                return ToHits(query, bm25Rows, options.TopK);
        }

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

        return ToHits(query, results, options.TopK);
    }

    /// <summary>
    /// Below this many postings (the query terms' summed df) every match is scored in one pass,
    /// which is cheaper than pruning rounds.
    /// </summary>
    internal const double ExhaustivePostings = 4000;

    /// <summary>Pruning rounds before the remaining matches are scored in full.</summary>
    private const int MaxPruningRounds = 6;

    /// <summary>
    /// Lucene BM25 over the owner's chunks that match the query (#548), exact.
    /// <para>
    /// Scoring every chunk that contains a common term is what makes naive SQL BM25 slow (seconds
    /// per 100k chunks). Instead each round asks the GIN index, through search_vector's frequency
    /// markers, for just the chunks whose score bound exceeds a threshold θ (see
    /// <see cref="Bm25Pruning"/>), skipping those already scored. Once the k-th best score reaches θ,
    /// every chunk left unscored is bounded by θ, so the top k is exact. θ starts high and drops to
    /// the k-th best score (or by 40% while fewer than k have been found), so a round or two
    /// usually suffices; after <see cref="MaxPruningRounds"/> the remaining matches are scored in full.
    /// </para>
    /// </summary>
    private async Task<List<KeywordSearchRow>> Bm25SearchAsync(
        string whereClause, string tsQuery, List<object> parameters, int topK, Guid ownerId,
        KeywordQuery parsed, SearchSettings bm25, CancellationToken ct)
    {
        List<Bm25Term> terms = await Bm25TermsAsync(ownerId, parsed, ct);
        if (terms.Count == 0)
            return [];
        if (terms.Sum(t => t.Df) <= ExhaustivePostings)
            return await Bm25TopKAsync(whereClause, tsQuery, parameters, topK, ownerId, terms, null, null, bm25, ct);

        // A term with no recorded minimum length gets 0, the loosest (always safe) bound.
        List<Bm25Pruning.Term> pruning = terms
            .Select(t => new Bm25Pruning.Term(t.Term, t.Weight, t.MaxTf, t.MinLen is > 0 and < int.MaxValue ? t.MinLen : 0))
            .ToList();
        List<double[]> bounds = pruning
            .Select(t => Bm25Pruning.LevelBounds(t, bm25.Bm25K1, bm25.Bm25B, terms[0].Avgdl))
            .ToList();

        double threshold = 0.7 * bounds.Sum(b => b.Length > 0 ? b[^1] : 0);
        var best = new Dictionary<Guid, KeywordSearchRow>();
        string? scored = null;

        for (int round = 0; round < MaxPruningRounds; round++)
        {
            List<Dictionary<int, int>>? clauses = Bm25Pruning.Clauses(bounds, threshold);
            if (clauses is null)
                break;

            string candidates = Bm25Pruning.ToTsQuery(clauses, pruning);
            foreach (KeywordSearchRow row in await Bm25TopKAsync(
                         whereClause, tsQuery, parameters, topK, ownerId, terms, candidates, scored, bm25, ct))
                best[row.ChunkId] = row;
            scored = scored is null ? candidates : $"{scored} | {candidates}";

            List<KeywordSearchRow> top = best.Values.OrderByDescending(r => r.Rank).Take(topK).ToList();
            if (top.Count == topK && top[^1].Rank >= threshold)
                return top;
            threshold = top.Count == topK ? top[^1].Rank : 0.6 * threshold;
        }

        foreach (KeywordSearchRow row in await Bm25TopKAsync(
                     whereClause, tsQuery, parameters, topK, ownerId, terms, null, scored, bm25, ct))
            best[row.ChunkId] = row;
        return best.Values.OrderByDescending(r => r.Rank).Take(topK).ToList();
    }

    /// <summary>
    /// The top <paramref name="topK"/> chunks by BM25 over <paramref name="terms"/> among the matches
    /// that also match the <paramref name="candidates"/> tsquery (null: any) and not the
    /// <paramref name="excluded"/> one (null: none).
    /// </summary>
    private async Task<List<KeywordSearchRow>> Bm25TopKAsync(
        string whereClause, string tsQuery, List<object> parameters, int topK, Guid ownerId,
        List<Bm25Term> terms, string? candidates, string? excluded, SearchSettings bm25, CancellationToken ct)
    {
        int ownerIdx = parameters.Count;
        List<object> bm25Parameters =
        [
            .. parameters, ownerId,
            terms.Select(t => t.Term).ToArray(), terms.Select(t => t.Weight).ToArray(),
            bm25.Bm25K1, bm25.Bm25B, terms[0].Avgdl, topK,
            candidates ?? "", excluded ?? "",
        ];
        string score = Bm25ScoreSql($"{{{ownerIdx + 3}}}", $"{{{ownerIdx + 4}}}", $"{{{ownerIdx + 5}}}");
        string candidateFilter = candidates is null ? "" : $"AND c.search_vector @@ {{{ownerIdx + 7}}}::tsquery";
        string excludedFilter = excluded is null ? "" : $"AND NOT (c.search_vector @@ {{{ownerIdx + 8}}}::tsquery)";

        // c.owner_id as well as d.owner_id: the statistics are the owner's, so the chunks must be.
        string sql = @$"
            SELECT
                c.id as ChunkId,
                c.document_id as DocumentId,
                c.content as Content,
                c.chunk_index as ChunkIndex,
                s.score::real as Rank,
                d.file_name as FileName,
                d.content_type as ContentType,
                d.owner_id as ContainerId,
                d.path as Path
            FROM (
                SELECT c.id, {score}
                FROM chunks c
                INNER JOIN documents d ON c.document_id = d.id
                {Bm25TermJoinSql($"{{{ownerIdx + 1}}}", $"{{{ownerIdx + 2}}}")}
                WHERE {whereClause}
                  AND c.owner_id = {{{ownerIdx}}}
                  AND c.search_vector @@ {tsQuery}
                  {candidateFilter}
                  {excludedFilter}
                GROUP BY c.id
                ORDER BY score DESC
                LIMIT {{{ownerIdx + 6}}}) s
            INNER JOIN chunks c ON c.id = s.id
            INNER JOIN documents d ON c.document_id = d.id
            ORDER BY s.score DESC";

        return await _context.Database
            .SqlQueryRaw<KeywordSearchRow>(sql, bm25Parameters.ToArray())
            .ToListAsync(ct);
    }

    /// <summary>
    /// The query's english terms with their BM25 weight (query frequency × IDF), df, highest
    /// frequency and shortest chunk, and the owner's N and avgdl. Empty when no term occurs in the
    /// owner's chunks.
    /// </summary>
    private async Task<List<Bm25Term>> Bm25TermsAsync(Guid ownerId, KeywordQuery parsed, CancellationToken ct)
    {
        string sql = $"""
            {Bm25StatsSql("{0}", "{1}")}
            SELECT q.term AS "Term", q.qtf * q.idf AS "Weight", q.df AS "Df", q.max_tf AS "MaxTf",
                   q.min_len AS "MinLen", os.n AS "N", os.avgdl AS "Avgdl"
            FROM q CROSS JOIN os
            """;
        return await _context.Database
            .SqlQueryRaw<Bm25Term>(sql, ownerId, parsed.Clauses.ToArray())
            .ToListAsync(ct);
    }

    private List<SearchHit> ToHits(string query, List<KeywordSearchRow> results, int topK)
    {
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
            topK);

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

        if (Bm25Settings is { } bm25)
        {
            // BM25 scores only mean something against one owner's statistics, so a pool spanning
            // owners (not something a container-scoped search produces) is scored with ts_rank.
            List<Guid> owners = await _context.Database
                .SqlQueryRaw<Guid>("SELECT DISTINCT owner_id AS \"Value\" FROM chunks WHERE id = ANY({0})", ids)
                .ToListAsync(ct);
            List<Bm25Term> terms = owners.Count == 1 ? await Bm25TermsAsync(owners[0], parsed, ct) : [];
            if (terms.Count > 0)
            {
                string bm25Sql = @$"
                    SELECT c.id as ChunkId, COALESCE(s.score, 0)::real as Rank
                    FROM chunks c
                    LEFT JOIN (
                        SELECT c.id, {Bm25ScoreSql("{5}", "{6}", "{7}")}
                        FROM chunks c
                        {Bm25TermJoinSql("{3}", "{4}")}
                        WHERE c.id = ANY({{2}}) AND c.search_vector @@ {TsQuerySql("{0}", "{1}")}
                        GROUP BY c.id) s ON s.id = c.id
                    WHERE c.id = ANY({{2}})";

                List<ChunkRankRow> bm25Rows = await _context.Database
                    .SqlQueryRaw<ChunkRankRow>(bm25Sql, parsed.Clauses.ToArray(), parsed.Exclusions.ToArray(), ids,
                        terms.Select(t => t.Term).ToArray(), terms.Select(t => t.Weight).ToArray(),
                        bm25.Bm25K1, bm25.Bm25B, terms[0].Avgdl)
                    .ToListAsync(ct);
                return bm25Rows.ToDictionary(r => r.ChunkId.ToString(), r => r.Rank);
            }
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
              (SELECT string_agg('(' || e::text || ')', ' | ') FILTER (WHERE numnode(e) > 0)::tsquery
               FROM unnest({exclusionsParam}::text[]) t, LATERAL phraseto_tsquery('english', t) e) n(q))
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
        // ts_filter to A and B drops the positionless BM25 frequency markers (#548), which would
        // otherwise count towards the length that normalization 1 divides by.
        $"ts_rank(ARRAY[0, 0, 1, 0]::float4[], ts_filter({vector}, ARRAY['a', 'b']::\"char\"[]), {tsQuery}, 1|32)";

    /// <summary>
    /// CTEs "q" (the query's terms with their BM25 IDF) and "os" (the owner's N and avgdl).
    /// Statistics are the folded tables plus any deltas not folded yet, so they are exact at all
    /// times: the chunks triggers write the deltas and Bm25StatsFolder folds them. Query terms are
    /// the english lexemes of every clause, a repeated term counted once per occurrence as Lucene
    /// does. IDF is Lucene's ln(1 + (N - df + 0.5) / (df + 0.5)), which is always positive.
    /// Ported from DuckDB fts match_bm25 (duckdb v1.1.3 extension/fts/fts_indexing.cpp, MIT,
    /// Copyright Stichting DuckDB Foundation); its log() is base 10, as PostgreSQL's is, hence ln().
    /// </summary>
    internal static string Bm25StatsSql(string ownerParam, string clausesParam) => $"""
        WITH qt AS MATERIALIZED (
            SELECT u.lexeme AS term, cardinality(u.positions)::float8 AS qtf
            FROM unnest(to_tsvector('english', array_to_string({clausesParam}::text[], ' '))) u),
        os AS MATERIALIZED (
            SELECT n, CASE WHEN n > 0 THEN total / n ELSE 1 END AS avgdl
            FROM (SELECT
                    (COALESCE((SELECT n_docs FROM bm25_owner_stats WHERE owner_id = {ownerParam}), 0)
                   + COALESCE((SELECT sum(d_n) FROM bm25_delta WHERE owner_id = {ownerParam} AND term IS NULL), 0))::float8 AS n,
                    (COALESCE((SELECT total_len FROM bm25_owner_stats WHERE owner_id = {ownerParam}), 0)
                   + COALESCE((SELECT sum(d_len) FROM bm25_delta WHERE owner_id = {ownerParam} AND term IS NULL), 0))::float8 AS total) t),
        q AS MATERIALIZED (
            SELECT qt.term, qt.qtf, x.df, x.max_tf, x.min_len, ln(1 + (os.n - x.df + 0.5) / (x.df + 0.5)) AS idf
            FROM qt CROSS JOIN os
            CROSS JOIN LATERAL (SELECT
                    (COALESCE(s.df, 0) + COALESCE(d.df, 0))::float8 AS df,
                    greatest(COALESCE(s.max_tf, 0), COALESCE(d.max_tf, 0)) AS max_tf,
                    least(COALESCE(s.min_len, 2147483647), COALESCE(d.min_len, 2147483647)) AS min_len
                FROM (SELECT 1) one
                LEFT JOIN bm25_term_stats s ON s.owner_id = {ownerParam} AND s.term = qt.term
                LEFT JOIN LATERAL (
                    SELECT sum(d_df) AS df, max(tf) AS max_tf, min(len) AS min_len
                    FROM bm25_delta WHERE owner_id = {ownerParam} AND term = qt.term) d ON true) x
            WHERE x.df > 0)
        """;

    /// <summary>
    /// Joins each chunk (alias c) to its term frequencies for the query's terms (a text[] parameter)
    /// and their weights (a float8[] parameter). Only the english (weight B) positions count:
    /// search_vector also holds a simple (A) copy of every word, and counting both would double every
    /// frequency. setweight + ts_filter keep just the query's lexemes before unnesting, in C, so a
    /// chunk costs its few query terms instead of its whole vocabulary.
    /// </summary>
    internal static string Bm25TermJoinSql(string termsParam, string weightsParam) => $"""
        CROSS JOIN LATERAL unnest(ts_filter(
            setweight(ts_filter(c.search_vector, ARRAY['b']::"char"[]), 'd', {termsParam}::text[]),
            ARRAY['d']::"char"[])) u
        INNER JOIN unnest({termsParam}::text[], {weightsParam}::float8[]) w(term, weight) ON w.term = u.lexeme
        """;

    /// <summary>
    /// Lucene BM25Similarity (lucene/core/src/java/org/apache/lucene/search/similarities/
    /// BM25Similarity.java, Apache-2.0): idf * f / (f + k1 * (1 - b + b * dl / avgdl)), summed over
    /// query terms, with the query-frequency × IDF weight precomputed per term. Lucene 8+ drops the
    /// textbook's constant (k1 + 1) factor; it rescales every score alike, so the ranking is the
    /// same. Chunk lengths are exact (chunks.bm25_length), not Lucene's lossy one-byte norms.
    /// </summary>
    internal static string Bm25ScoreSql(string k1Param, string bParam, string avgdlParam) => $"""
        sum(w.weight * cardinality(u.positions)
            / (cardinality(u.positions) + {k1Param}::float8 * (1 - {bParam}::float8
               + {bParam}::float8 * c.bm25_length / {avgdlParam}::float8))) AS score
        """;

    private record Bm25Term(string Term, double Weight, double Df, int MaxTf, int MinLen, double N, double Avgdl);

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
