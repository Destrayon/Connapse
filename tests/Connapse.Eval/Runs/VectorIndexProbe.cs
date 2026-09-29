using System.Data.Common;
using Connapse.Core;
using Connapse.Eval.Cli;
using Connapse.Eval.Datasets;
using Connapse.Eval.Metrics;
using Connapse.Eval.Model;
using Connapse.Eval.Systems;
using Connapse.Search.Vector;
using Connapse.Storage.Vectors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pgvector;

namespace Connapse.Eval.Runs;

/// <summary>
/// How much production's vector index loses against exact search (#570). Ingests a suite into one
/// instance (one container per dataset, one shared per-model index, as in production), builds the
/// index the way <see cref="VectorColumnManager"/> does, then runs each query's production vector-search
/// SQL several ways: as production runs it, exact (index scans off), and at several ivfflat.probes.
/// Reports recall@30 of each against exact, how many hits came back, the plan, and nDCG@10.
/// </summary>
public sealed class VectorIndexProbe(RepoPaths paths, TextWriter log, HttpClient http)
{
    private const int TopK = 30;
    private static readonly int[] Probes = [1, 5, 10, 20];

    // PgVectorStore.SearchAsync's statement for an unrestricted search in one container, one model.
    private const string SearchSql = """
        SELECT cv.chunk_id, cv.document_id
        FROM chunk_vectors cv
        INNER JOIN chunks c ON cv.chunk_id = c.id
        INNER JOIN documents d ON cv.document_id = d.id
        WHERE 1=1 AND NOT EXISTS (SELECT 1 FROM sources s WHERE s.id = d.source_id AND s.access_revoked_at IS NOT NULL)
          AND cv.owner_id = @containerId AND cv.model_id = @modelId
        ORDER BY (cv.embedding::vector({0}) <=> @queryVector) ASC
        LIMIT @topK
        """;

    // Candidate shape (#571): nearest neighbours from chunk_vectors alone, where a vector index can
    // serve the ORDER BY … LIMIT, then the joins and document filters over an over-fetched list.
    private const string NeighboursFirstSql = """
        WITH nn AS MATERIALIZED (
            SELECT cv.chunk_id, cv.document_id, cv.embedding::vector({0}) <=> @queryVector AS distance
            FROM chunk_vectors cv
            WHERE cv.owner_id = @containerId AND cv.model_id = @modelId
            ORDER BY cv.embedding::vector({0}) <=> @queryVector
            LIMIT @topK * 4)
        SELECT nn.chunk_id, nn.document_id
        FROM nn
        INNER JOIN chunks c ON nn.chunk_id = c.id
        INNER JOIN documents d ON nn.document_id = d.id
        WHERE NOT EXISTS (SELECT 1 FROM sources s WHERE s.id = d.source_id AND s.access_revoked_at IS NOT NULL)
        ORDER BY nn.distance
        LIMIT @topK
        """;

    public async Task<int> RunAsync(string suite, IReadOnlyList<string> only, int? limitQueries, CancellationToken ct)
    {
        EvalManifest manifest = EvalManifest.Load(paths.ManifestPath);
        IReadOnlyList<string> names = manifest.ResolveSuite(suite, only);
        DatasetCache cache = new(paths.CacheRoot, http, paths.DatasetsRoot);
        foreach (string name in names)
            await cache.EnsureAsync(name, manifest.Datasets[name], allowUnpinned: false, ct);

        SystemConfig config = SystemConfig.Load(paths.EvalRoot, "connapse", "semantic");
        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        await using ConnapseSearchSystem system = await ConnapseSearchSystem.StartAsync(
            config, paths.WebContentRoot, embeddings, log, null, ct);

        List<(EvalDataset Dataset, Guid ContainerId, IReadOnlyDictionary<string, string> DocMap)> loaded = [];
        foreach (string name in names)
        {
            DatasetEntry entry = manifest.Datasets[name];
            EvalDataset dataset = await DatasetAdapters.Get(entry.Adapter).LoadAsync(name, entry, cache.DirectoryFor(name, entry), ct);
            log.WriteLine($"[{name}] indexing {dataset.Corpus.Count} documents");
            await system.IndexAsync(dataset, ct);
            (Guid containerId, IReadOnlyDictionary<string, string> docMap) = system.ContainerOf(name);
            loaded.Add((dataset, containerId, docMap));
        }

        // Production builds its indexes at startup and on an embedding-settings change; do that now.
        await using (AsyncServiceScope scope = system.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<VectorColumnManager>().EnsureIndexesAsync(ct);

        await using AsyncServiceScope services = system.Services.CreateAsyncScope();
        // From configuration: the one EF hands back has its password stripped.
        string connectionString = services.ServiceProvider.GetRequiredService<IConfiguration>()
            .GetConnectionString("DefaultConnection")!;
        VectorSearchService vectorSearch = services.ServiceProvider.GetRequiredService<VectorSearchService>();

        NpgsqlDataSourceBuilder builder = new(connectionString);
        builder.UseVector();
        await using NpgsqlDataSource dataSource = builder.Build();
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(ct);
        // Planner statistics as a settled database has them (autovacuum analyzes it in time).
        await ExecAsync(connection, "ANALYZE chunk_vectors; ANALYZE chunks; ANALYZE documents; ANALYZE sources;", ct);
        log.WriteLine(await IndexSummaryAsync(connection, ct));

        foreach ((EvalDataset dataset, Guid containerId, IReadOnlyDictionary<string, string> docMap) in loaded)
        {
            IReadOnlyList<EvalQuery> queries = limitQueries is int limit ? dataset.Queries.Take(limit).ToList() : dataset.Queries;
            Dictionary<string, List<double>> recall = new(), returned = new(), ndcg = new();
            string? plan = null;
            long vectors = await ScalarAsync(connection, "SELECT count(*) FROM chunk_vectors WHERE owner_id = @c", containerId, ct);

            foreach (EvalQuery query in queries)
            {
                SearchOptions options = new(TopK: TopK, ContainerId: containerId.ToString(), Mode: SearchMode.Semantic);
                QueryEmbedding embedding = await vectorSearch.EmbedQueryAsync(query.Text, options, ct);
                QuerySpace space = embedding.Spaces[0];
                plan ??= await ExplainAsync(connection, space, containerId, ct);

                List<(string Chunk, string Doc)> exact = await SearchAsync(connection, space, containerId, "exact", ct);
                HashSet<string> exactChunks = exact.Select(e => e.Chunk).ToHashSet();
                List<SearchHit> service = await vectorSearch.SearchAsync(query.Text, embedding, options, SearchScopes.Unrestricted, ct);
                Record("service", service.Select(h => (h.ChunkId, h.DocumentId)).ToList());
                Record("production", await SearchAsync(connection, space, containerId, "production", ct));
                foreach (int p in Probes)
                    Record($"probes {p}", await SearchAsync(connection, space, containerId, $"probes:{p}", ct));
                Record("exact", exact);

                void Record(string variant, List<(string Chunk, string Doc)> hits)
                {
                    Add(recall, variant, exactChunks.Count == 0 ? 1 : hits.Count(h => exactChunks.Contains(h.Chunk)) / (double)exactChunks.Count);
                    Add(returned, variant, hits.Count);
                    IReadOnlyList<RankedDoc> ranked = Collapse(hits, docMap);
                    if (RankingMetrics.Score(ranked, dataset.Qrels.For(query.Id)) is { } scores)
                        Add(ndcg, variant, scores[MetricNames.Ndcg10]);
                }
            }

            log.WriteLine($"[{dataset.Name}] {vectors} vectors in container, {queries.Count} queries; plan: {plan}");
            foreach (string variant in recall.Keys)
                log.WriteLine($"  {variant,-11} recall@{TopK} {recall[variant].Average():F4}  hits {returned[variant].Average():F1}  "
                    + $"min hits {returned[variant].Min():F0}  nDCG@10 {(ndcg.TryGetValue(variant, out var n) ? n.Average() : double.NaN):F4}");
        }
        return 0;
    }

    private static void Add(Dictionary<string, List<double>> into, string key, double value)
    {
        if (!into.TryGetValue(key, out List<double>? list))
            into[key] = list = [];
        list.Add(value);
    }

    private static IReadOnlyList<RankedDoc> Collapse(List<(string Chunk, string Doc)> hits, IReadOnlyDictionary<string, string> docMap)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<RankedDoc> ranked = [];
        foreach ((_, string doc) in hits)
            if (docMap.TryGetValue(doc, out string? id) && seen.Add(id))
            {
                ranked.Add(new RankedDoc(id, 1.0 / (ranked.Count + 1)));
                if (ranked.Count == EvalRunner.K)
                    break;
            }
        return ranked;
    }

    private static async Task<List<(string Chunk, string Doc)>> SearchAsync(
        NpgsqlConnection connection, QuerySpace space, Guid containerId, string variant, CancellationToken ct)
    {
        await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(ct);
        if (variant == "exact")
            await ExecAsync(connection, "SET LOCAL enable_indexscan = off; SET LOCAL enable_bitmapscan = off;", ct);
        else if (variant.StartsWith("probes:", StringComparison.Ordinal))
            await ExecAsync(connection, $"SET LOCAL ivfflat.probes = {int.Parse(variant[7..])};", ct);
        else if (variant.StartsWith("hnsw:", StringComparison.Ordinal) || variant.StartsWith("nn:", StringComparison.Ordinal))
            await ExecAsync(connection,
                $"SET LOCAL hnsw.iterative_scan = relaxed_order; SET LOCAL hnsw.ef_search = {int.Parse(variant[(variant.IndexOf(':') + 1)..])};", ct);

        string sql = variant.StartsWith("nn:", StringComparison.Ordinal) ? NeighboursFirstSql : SearchSql;
        await using NpgsqlCommand cmd = Command(connection, string.Format(sql, space.Vector.Length), space, containerId);
        List<(string, string)> hits = [];
        await using (DbDataReader reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                hits.Add((reader.GetGuid(0).ToString(), reader.GetGuid(1).ToString()));
        await tx.RollbackAsync(ct);
        return hits;
    }

    /// <summary>Chunk IDs the production query returns for a raw query vector (the scale probe's entry point).</summary>
    internal static async Task<List<string>> SearchAsync(
        NpgsqlConnection connection, float[] query, Guid containerId, string modelId, string variant, CancellationToken ct) =>
        (await SearchAsync(connection, new QuerySpace(query, modelId), containerId, variant, ct)).Select(h => h.Chunk).ToList();

    internal static Task<string> ExplainAsync(
        NpgsqlConnection connection, float[] query, Guid containerId, string modelId, CancellationToken ct) =>
        ExplainAsync(connection, new QuerySpace(query, modelId), containerId, ct);

    internal static async Task<string> ExplainNeighboursFirstAsync(
        NpgsqlConnection connection, float[] query, Guid containerId, string modelId, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = Command(connection, "EXPLAIN " + string.Format(NeighboursFirstSql, query.Length),
            new QuerySpace(query, modelId), containerId);
        List<string> lines = [];
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            lines.Add(reader.GetString(0));
        return string.Join(" | ", lines.Where(l => l.Contains("Scan", StringComparison.Ordinal)).Select(l => l.Trim()));
    }

    private static async Task<string> ExplainAsync(NpgsqlConnection connection, QuerySpace space, Guid containerId, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = Command(connection, "EXPLAIN " + string.Format(SearchSql, space.Vector.Length), space, containerId);
        List<string> lines = [];
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            lines.Add(reader.GetString(0));
        return string.Join(" | ", lines.Where(l => l.Contains("Scan", StringComparison.Ordinal)).Select(l => l.Trim()));
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, QuerySpace space, Guid containerId)
    {
        NpgsqlCommand cmd = new(sql, connection) { CommandTimeout = 0 };
        cmd.Parameters.AddWithValue("containerId", containerId);
        cmd.Parameters.AddWithValue("modelId", space.ModelId);
        cmd.Parameters.AddWithValue("queryVector", new Vector(space.Vector));
        cmd.Parameters.AddWithValue("topK", TopK);
        return cmd;
    }

    private static async Task<string> IndexSummaryAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = new(
            "SELECT indexname, indexdef FROM pg_indexes WHERE tablename = 'chunk_vectors' AND indexname LIKE 'idx_cv_emb_%'", connection);
        List<string> lines = ["vector indexes:"];
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            lines.Add($"  {reader.GetString(0)}: {reader.GetString(1)}");
        return string.Join(Environment.NewLine, lines);
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = new(sql, connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql, Guid containerId, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("c", containerId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }
}
