using System.Diagnostics;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Eval.Cli;
using Connapse.Eval.Datasets;
using Connapse.Eval.Model;
using Connapse.Eval.Systems;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Eval.Runs;

/// <summary>
/// What search scores say about relevance (CON-278, eval/pass-bars.md). Searches each dataset of a suite with
/// its own queries (in-domain) and with the next dataset's queries (foreign, which nothing in the corpus should
/// answer), the way a client does: one search, the top 10 chunks, the app's settings. Writes one JSON line per
/// query with its duration, the result's no-match flag and every hit's score, metadata and relevance grade;
/// eval/tools/score_calibration.py turns the lines into the bars' numbers.
/// </summary>
public sealed class ScoresProbe(RepoPaths paths, TextWriter log, HttpClient http)
{
    private const int TopK = 10;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public sealed record Options(
        string Suite, string Config, IReadOnlyList<string> Only, int? LimitQueries, string Split, string? IndexKey, string? Out);

    public async Task<int> RunAsync(Options options, CancellationToken ct)
    {
        bool fresh = options.Split == "fresh";
        Split? split = options.Split switch
        {
            "test" => Model.Split.Test,
            "dev" => Model.Split.Dev,
            "all" or "fresh" => null,
            _ => throw new ArgumentException($"--split takes test, dev, all or fresh (got '{options.Split}')."),
        };

        EvalManifest manifest = EvalManifest.Load(paths.ManifestPath);
        IReadOnlyList<string> names = manifest.ResolveSuite(options.Suite, options.Only);
        if (names.Count < 2)
            throw new ArgumentException("Foreign queries come from another dataset; give at least two.");
        DatasetCache cache = new(paths.CacheRoot, http, paths.DatasetsRoot);
        Dictionary<string, IReadOnlyDictionary<string, string>> hashes = new(StringComparer.Ordinal);
        foreach (string name in names)
            hashes[name] = await cache.EnsureAsync(name, manifest.Datasets[name], allowUnpinned: false, ct);

        SystemConfig config = SystemConfig.Load(paths.EvalRoot, "connapse", options.Config);
        // A given key reuses one saved index for runs on different code, so both search the same chunks.
        string key = options.IndexKey ?? IndexCacheKey.Compute(config.IndexOnly(),
            names.Select(n => (n, manifest.Datasets[n].Version, hashes[n])),
            IndexCacheKey.SourceHash(paths.RepoRoot), IndexCacheKey.CurrentEnvironment());
        log.WriteLine($"index cache key {key}");
        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        await using ConnapseSearchSystem system = await ConnapseSearchSystem.StartAsync(config, paths.WebContentRoot, embeddings, log,
            null, ct, new IndexCacheStore(Path.Combine(paths.CacheRoot, "index-snapshots"), key));

        List<EvalDataset> datasets = [];
        foreach (string name in names)
        {
            DatasetEntry entry = manifest.Datasets[name];
            EvalDataset dataset = await DatasetAdapters.Get(entry.Adapter).LoadAsync(name, entry, cache.DirectoryFor(name, entry), ct);
            if (system.RestoredIndex(name) is null)
            {
                log.WriteLine($"[{name}] indexing {dataset.Corpus.Count} documents");
                await system.IndexAsync(dataset, ct);
            }
            datasets.Add(fresh ? await FreshAsync(dataset, cache.DirectoryFor(name, entry), ct) : dataset);
        }
        if (!system.Restored)
            await system.SaveIndexCacheAsync(names, ct);

        string outPath = options.Out ?? Path.Combine(paths.RunsRoot,
            $"scores-{options.Suite}-{options.Config}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await using StreamWriter writer = new(outPath);

        for (int i = 0; i < datasets.Count; i++)
        {
            EvalDataset dataset = datasets[i];
            EvalDataset foreign = datasets[(i + 1) % datasets.Count];
            (Guid containerId, IReadOnlyDictionary<string, string> docMap) = system.ContainerOf(dataset.Name);
            List<double> durations = [];
            int noMatch = 0;

            foreach ((string kind, EvalDataset source) in new[] { ("in", dataset), ("foreign", foreign) })
            {
                IEnumerable<EvalQuery> queries = source.Queries.Where(q => split is null || q.Split == split);
                if (options.LimitQueries is int limit)
                    queries = queries.Take(limit);
                foreach (EvalQuery query in queries)
                {
                    await using AsyncServiceScope scope = system.Services.CreateAsyncScope();
                    IKnowledgeSearch search = scope.ServiceProvider.GetRequiredService<IKnowledgeSearch>();
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    SearchResult result = await search.SearchAsync(query.Text,
                        new SearchOptions(TopK: TopK, ContainerId: containerId.ToString(), Mode: config.SearchMode), ct);
                    double ms = stopwatch.Elapsed.TotalMilliseconds;

                    IReadOnlyDictionary<string, int> grades = kind == "in" ? dataset.Qrels.For(query.Id) : new Dictionary<string, int>();
                    var hits = result.Hits.Select((h, rank) =>
                    {
                        string? doc = docMap.GetValueOrDefault(h.DocumentId);
                        return new
                        {
                            Rank = rank + 1,
                            h.ChunkId,
                            Doc = doc,
                            h.Score,
                            Grade = doc is not null && grades.TryGetValue(doc, out int g) ? g : 0,
                            // Scores and tags only; the chunk text would make the file hundreds of megabytes.
                            Metadata = h.Metadata.Where(m => m.Key is not ("content" or "window"))
                                .ToDictionary(m => m.Key, m => m.Value),
                        };
                    }).ToList();
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        Dataset = dataset.Name,
                        QueryDataset = source.Name,
                        QueryId = query.Id,
                        Split = fresh ? "fresh" : query.Split.ToString().ToLowerInvariant(),
                        Kind = kind,
                        DurationMs = ms,
                        result.NoGoodMatch,
                        Judged = grades.Count > 0,
                        Hits = hits,
                    }, Json));
                    if (kind == "in")
                        durations.Add(ms);
                    else if (result.NoGoodMatch)
                        noMatch++;
                }
            }

            durations.Sort();
            log.WriteLine($"[{dataset.Name}] {durations.Count} in-domain queries, p95 "
                + $"{(durations.Count == 0 ? 0 : durations[(int)Math.Ceiling(0.95 * durations.Count) - 1]):F0} ms; "
                + $"no-match on {noMatch} foreign queries");
        }

        log.WriteLine(outPath);
        return 0;
    }
    /// <summary>Questions per dataset in the fresh split.</summary>
    private const int FreshSize = 300;

    /// <summary>
    /// The fresh split (CON-278): RAGBench train questions, which no run has used as queries (the adapter loads train
    /// rows only for their documents, which are indexed). Each dataset's distinct train questions, minus any whose ID or
    /// text is also a dev or test question, are ordered by SHA-256 of "con-278-fresh:" + ID and the first
    /// <see cref="FreshSize"/> kept. Relevance comes from the rows' sentence keys, read as the adapter reads them.
    /// </summary>
    private static async Task<EvalDataset> FreshAsync(EvalDataset dataset, string directory, CancellationToken ct)
    {
        IList<RagBenchRow> rows = (await Parquet.Serialization.ParquetSerializer.DeserializeAsync<RagBenchRow>(
            Path.Combine(directory, "train.parquet"), cancellationToken: ct)).Data;
        HashSet<string> used = new(StringComparer.Ordinal);
        foreach (EvalQuery q in dataset.Queries)
        {
            used.Add(q.Id);
            used.Add(q.Text);
        }

        Dictionary<string, RagBenchRow> byId = new(StringComparer.Ordinal);
        foreach (RagBenchRow row in rows)
            if (!used.Contains(row.Id) && !used.Contains(row.Question))
                byId.TryAdd(row.Id, row);

        List<RagBenchRow> picked = byId.Values
            .OrderBy(r => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("con-278-fresh:" + r.Id))), StringComparer.Ordinal)
            .Take(FreshSize)
            .ToList();

        Qrels qrels = new();
        foreach (RagBenchRow row in picked)
            foreach (string key in row.AllRelevantSentenceKeys ?? [])
            {
                int length = 0;
                while (length < key.Length && char.IsAsciiDigit(key[length]))
                    length++;
                if (length == 0)
                    continue;
                int index = int.Parse(key.AsSpan(0, length), System.Globalization.CultureInfo.InvariantCulture);
                if (index < row.Documents.Count)
                    qrels.Add(row.Id, RagBenchAdapter.DocId(row.Documents[index]), 1);
            }

        return dataset with
        {
            Queries = [.. picked.Select(r => new EvalQuery(r.Id, r.Question, Model.Split.Test, []))],
            Qrels = qrels,
        };
    }
}
