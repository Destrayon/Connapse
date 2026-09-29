using System.Data.Common;
using System.Diagnostics;
using Connapse.Core;
using Connapse.Eval.Cli;
using Connapse.Eval.Systems;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Storage.Vectors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Parquet.Serialization;
using Pgvector;

namespace Connapse.Eval.Runs;

/// <summary>
/// The vector-index probe at scale (#571): loads a prebuilt scale set (eval/tools/build_scale_set.py:
/// real embeddings in topically coherent containers of 1k–1M vectors) into a fresh Connapse database
/// through its own entities, builds the indexes the way production does, then for each measured
/// container runs the production vector-search SQL against exact search and reports recall@30,
/// completeness and latency.
/// </summary>
public sealed class VectorIndexScaleProbe(RepoPaths paths, TextWriter log)
{
    private const int TopK = 30;
    private const int Dims = 768;
    private const int Batch = 5_000;

    public async Task<int> RunAsync(string scaleDir, int queriesPerContainer, bool strategy, bool insertBench, CancellationToken ct)
    {
        string dir = Path.IsPathRooted(scaleDir) ? scaleDir : Path.Combine(paths.RepoRoot, scaleDir);
        IList<ScalePassage> passages = (await ParquetSerializer.DeserializeAsync<ScalePassage>(Path.Combine(dir, "passages.parquet"), cancellationToken: ct)).Data;
        float[][] queries = ReadVectors(Path.Combine(dir, "queries.f32")).Take(queriesPerContainer).ToArray();

        SystemConfig config = SystemConfig.Load(paths.EvalRoot, "connapse", "semantic");
        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        await using ConnapseSearchSystem system = await ConnapseSearchSystem.StartAsync(
            config, paths.WebContentRoot, embeddings, log, null, ct);
        string modelId = EmbeddingIdentity.For(system.Services.GetRequiredService<IOptionsMonitor<EmbeddingSettings>>().CurrentValue);

        int containerCount = passages.Max(p => p.Container) + 1;
        Guid[] containers = Enumerable.Range(0, containerCount).Select(_ => Guid.NewGuid()).ToArray();
        await using (AsyncServiceScope loadScope = system.Services.CreateAsyncScope())
            await LoadAsync(loadScope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>(),
                dir, passages, containers, modelId, ct);

        await using (AsyncServiceScope scope = system.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<VectorColumnManager>().EnsureIndexesAsync(ct, waitForOthers: true);

        NpgsqlDataSourceBuilder builder = new(system.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection"));
        builder.UseVector();
        await using NpgsqlDataSource dataSource = builder.Build();
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync("ANALYZE chunk_vectors; ANALYZE chunks; ANALYZE documents; ANALYZE sources;", ct);

        // Measure every container of a distinct size class: each named size once, plus three 1k fillers.
        Dictionary<int, int> sizes = passages.GroupBy(p => p.Container).ToDictionary(g => g.Key, g => g.Count());
        List<int> measured = sizes.OrderByDescending(s => s.Value).GroupBy(s => s.Value).SelectMany(g => g.Take(3)).Select(s => s.Key).ToList();
        log.WriteLine($"loaded {passages.Count} vectors in {containerCount} containers; measuring {measured.Count} containers × {queries.Length} queries");

        if (insertBench)
        {
            // Ingestion cost with and without a container index (#571): the same 5,000 vectors
            // inserted the way the pipeline stores them, in document-sized batches.
            float[][] extra = ReadVectors(Path.Combine(dir, "vectors.f32")).Take(5_000).ToArray();
            int indexed = sizes.MaxBy(s => s.Value).Key;
            int plain = sizes.Where(s => s.Value < 20_000).MaxBy(s => s.Value).Key;
            await using AsyncServiceScope benchScope = system.Services.CreateAsyncScope();
            IDbContextFactory<KnowledgeDbContext> factory = benchScope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
            foreach ((int c, string label) in new[] { (plain, "no index"), (indexed, "HNSW index"), (plain, "no index (again)") })
            {
                Stopwatch timer = Stopwatch.StartNew();
                await InsertAsync(factory, containers[c], modelId, extra, ct);
                log.WriteLine($"insert: container {c} ({sizes[c]} vectors, {label}): {extra.Length} vectors in {timer.Elapsed.TotalSeconds:F1}s = "
                    + $"{timer.Elapsed.TotalMilliseconds / extra.Length:F2} ms/vector");
            }
            return 0;
        }

        // Ground truth once per (container, query): exact top-30 with index scans off.
        Dictionary<(int, int), HashSet<string>> truth = [];
        foreach (int c in measured)
            for (int q = 0; q < queries.Length; q++)
                truth[(c, q)] = (await VectorIndexProbe.SearchAsync(connection, queries[q], containers[c], modelId, "exact", ct)).ToHashSet();

        // The production shape takes seconds per query on large containers; measure it only without --strategy.
        if (!strategy)
        {
            await MeasureAsync("production", "production");
            return 0;
        }

        // The product path (#571): the indexes VectorColumnManager built after loading, searched
        // through PgVectorStore, at several hnsw.ef_search values.
        foreach (int ef in new[] { 200, 400, 800 })
            await MeasureProductAsync(ef);
        return 0;

        async Task MeasureProductAsync(int ef)
        {
            log.WriteLine($"== product ef_search {ef}");
            await using AsyncServiceScope scope = system.Services.CreateAsyncScope();
            PgVectorStore store = new(scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>(),
                new FixedOptions<SearchSettings>(new SearchSettings { VectorIndexEfSearch = ef }),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PgVectorStore>.Instance);
            foreach (int c in measured)
            {
                List<double> recall = [], hits = [], latency = [];
                for (int q = 0; q < queries.Length; q++)
                {
                    Dictionary<string, string> filters = new() { ["containerId"] = containers[c].ToString(), ["modelId"] = modelId };
                    Stopwatch timer = Stopwatch.StartNew();
                    IReadOnlyList<VectorSearchResult> found = await store.SearchAsync(queries[q], TopK, filters, SearchScopes.Unrestricted, ct);
                    latency.Add(timer.Elapsed.TotalMilliseconds);
                    HashSet<string> expected = truth[(c, q)];
                    recall.Add(expected.Count == 0 ? 1 : found.Count(h => expected.Contains(h.Id)) / (double)expected.Count);
                    hits.Add(found.Count);
                }
                log.WriteLine($"container {c}: {sizes[c]} vectors  recall@{TopK} {recall.Average():F4} (min {recall.Min():F2})  "
                    + $"hits {hits.Average():F1} (min {hits.Min():F0})  p50 {Percentile(latency, 50):F0} ms p95 {Percentile(latency, 95):F0} ms");
            }
        }

        async Task MeasureAsync(string label, string variant)
        {
            log.WriteLine($"== {label}");
            foreach (int c in measured)
            {
                List<double> recall = [], hits = [], latency = [];
                string? plan = null;
                for (int q = 0; q < queries.Length; q++)
                {
                    plan ??= variant.StartsWith("nn:", StringComparison.Ordinal)
                        ? await VectorIndexProbe.ExplainNeighboursFirstAsync(connection, queries[q], containers[c], modelId, ct)
                        : await VectorIndexProbe.ExplainAsync(connection, queries[q], containers[c], modelId, ct);
                    Stopwatch timer = Stopwatch.StartNew();
                    List<string> found = await VectorIndexProbe.SearchAsync(connection, queries[q], containers[c], modelId, variant, ct);
                    latency.Add(timer.Elapsed.TotalMilliseconds);
                    HashSet<string> expected = truth[(c, q)];
                    recall.Add(expected.Count == 0 ? 1 : found.Count(expected.Contains) / (double)expected.Count);
                    hits.Add(found.Count);
                }
                log.WriteLine($"container {c}: {sizes[c]} vectors  recall@{TopK} {recall.Average():F4} (min {recall.Min():F2})  "
                    + $"hits {hits.Average():F1} (min {hits.Min():F0})  p50 {Percentile(latency, 50):F0} ms p95 {Percentile(latency, 95):F0} ms  plan: {plan}");
            }
        }
    }

    /// <summary>Inserts vectors into a container in batches of 20 documents, as ingestion stores them.</summary>
    private static async Task InsertAsync(
        IDbContextFactory<KnowledgeDbContext> factory, Guid owner, string modelId, float[][] vectors, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        for (int start = 0; start < vectors.Length; start += 20)
        {
            await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
            for (int i = start; i < Math.Min(start + 20, vectors.Length); i++)
            {
                Guid doc = Guid.NewGuid(), chunk = Guid.NewGuid();
                context.Documents.Add(new DocumentEntity
                {
                    Id = doc, ContainerId = owner, FileName = $"bench-{doc:N}.txt", ContentType = "text/plain",
                    Path = $"/bench/{doc:N}.txt", ContentHash = doc.ToString("N"), SizeBytes = 1, ChunkCount = 1,
                    IngestionStatus = DocumentStatus.Ready, StatusChangedAt = now, CreatedAt = now,
                });
                context.Chunks.Add(new ChunkEntity { Id = chunk, DocumentId = doc, OwnerId = owner, Content = "", TokenCount = 1 });
                context.ChunkVectors.Add(new ChunkVectorEntity
                {
                    ChunkId = chunk, DocumentId = doc, OwnerId = owner, Embedding = new Vector(vectors[i]),
                    ModelId = modelId, Dimensions = Dims,
                });
            }
            await context.SaveChangesAsync(ct);
        }
    }

    private async Task LoadAsync(
        IDbContextFactory<KnowledgeDbContext> factory, string dir, IList<ScalePassage> passages, Guid[] containers,
        string modelId, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        await using (KnowledgeDbContext context = await factory.CreateDbContextAsync(ct))
        {
            context.Containers.AddRange(containers.Select((id, i) =>
                new ContainerEntity { Id = id, Name = $"scale-{i:D4}", CreatedAt = now, UpdatedAt = now }));
            await context.SaveChangesAsync(ct);
        }

        Stopwatch timer = Stopwatch.StartNew();
        using IEnumerator<float[]> vectors = ReadVectors(Path.Combine(dir, "vectors.f32")).GetEnumerator();
        for (int start = 0; start < passages.Count; start += Batch)
        {
            await using KnowledgeDbContext context = await factory.CreateDbContextAsync(ct);
            context.ChangeTracker.AutoDetectChangesEnabled = false;
            for (int i = start; i < Math.Min(start + Batch, passages.Count); i++)
            {
                vectors.MoveNext();
                Guid owner = containers[passages[i].Container], doc = Guid.NewGuid(), chunk = Guid.NewGuid();
                context.Documents.Add(new DocumentEntity
                {
                    Id = doc, ContainerId = owner, FileName = passages[i].Id + ".txt", ContentType = "text/plain",
                    Path = "/" + passages[i].Id + ".txt", ContentHash = passages[i].Id, SizeBytes = 1, ChunkCount = 1,
                    IngestionStatus = DocumentStatus.Ready, StatusChangedAt = now, CreatedAt = now, LastIndexedAt = now,
                });
                context.Chunks.Add(new ChunkEntity { Id = chunk, DocumentId = doc, OwnerId = owner, Content = "", TokenCount = 1 });
                context.ChunkVectors.Add(new ChunkVectorEntity
                {
                    ChunkId = chunk, DocumentId = doc, OwnerId = owner, Embedding = new Vector(vectors.Current),
                    ModelId = modelId, ContentHash = passages[i].Id, Dimensions = Dims,
                });
            }
            await context.SaveChangesAsync(ct);
            if (start / Batch % 20 == 0)
                log.WriteLine($"  loaded {Math.Min(start + Batch, passages.Count)}/{passages.Count} ({timer.Elapsed.TotalSeconds:F0}s)");
        }
    }

    private static IEnumerable<float[]> ReadVectors(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] buffer = new byte[Dims * sizeof(float)];
        while (stream.Read(buffer, 0, buffer.Length) == buffer.Length)
        {
            float[] vector = new float[Dims];
            Buffer.BlockCopy(buffer, 0, vector, 0, buffer.Length);
            yield return vector;
        }
    }

    private static double Percentile(List<double> values, double p)
    {
        List<double> sorted = values.Order().ToList();
        return sorted[Math.Clamp((int)Math.Ceiling(p / 100 * sorted.Count) - 1, 0, sorted.Count - 1)];
    }

    public sealed class ScalePassage
    {
        [System.Text.Json.Serialization.JsonPropertyName("_id")]
        public string Id { get; set; } = "";

        // pyarrow writes columns as nullable
        [System.Text.Json.Serialization.JsonPropertyName("container")]
        public int? ContainerIndex { get; set; }

        public int Container => ContainerIndex ?? throw new InvalidDataException($"passage {Id} has no container");
    }
}

internal static class NpgsqlConnectionExtensions
{
    public static async Task ExecuteAsync(this NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = new(sql, connection);
        cmd.CommandTimeout = 0;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

internal sealed class FixedOptions<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
