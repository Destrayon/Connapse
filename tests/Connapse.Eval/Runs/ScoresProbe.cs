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
        Split? split = options.Split switch
        {
            "test" => Model.Split.Test,
            "dev" => Model.Split.Dev,
            "all" => null,
            _ => throw new ArgumentException($"--split takes test, dev or all (got '{options.Split}')."),
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
            datasets.Add(dataset);
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
                        Split = query.Split.ToString().ToLowerInvariant(),
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
}
