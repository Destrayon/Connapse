using Connapse.Eval.Cli;
using Connapse.Eval.Datasets;
using Connapse.Eval.Model;
using Connapse.Eval.Systems;

namespace Connapse.Eval.Runs;

public sealed record RunRequest(
    string Suite,
    string System,
    string Config,
    IReadOnlyList<string> OnlyDatasets,
    string? ResumeDir,
    int? LimitQueries);

public sealed class EvalRunner(
    RepoPaths paths,
    TextWriter log,
    HttpClient http,
    Func<SystemConfig, CancellationToken, Task<ISystemUnderTest>> systemFactory)
{
    public const int K = 10;
    private const double MaxFailedFraction = 0.01;

    public async Task<RunFolder> RunAsync(RunRequest request, CancellationToken ct)
    {
        EvalManifest manifest = EvalManifest.Load(paths.ManifestPath);
        IReadOnlyList<string> names = manifest.ResolveSuite(request.Suite, request.OnlyDatasets);
        DatasetCache cache = new(paths.CacheRoot, http);

        // Verify every file before starting containers, so a checksum problem fails in seconds.
        Dictionary<string, IReadOnlyDictionary<string, string>> hashes = new(StringComparer.Ordinal);
        foreach (string name in names)
            hashes[name] = await cache.EnsureAsync(name, manifest.Datasets[name], allowUnpinned: false, ct);

        SystemConfig config = SystemConfig.Load(paths.EvalRoot, request.System, request.Config);
        (RunFolder run, RunManifest runManifest) = OpenOrCreate(request, config);

        List<string> pending = names.Where(n => !run.IsDatasetComplete(n)).ToList();
        if (pending.Count > 0)
        {
            await using ISystemUnderTest system = await systemFactory(config, ct);
            IReadOnlyDictionary<string, string> description = system.Describe();
            if (runManifest.SystemDescription.Count > 0 && !SameDescription(runManifest.SystemDescription, description))
                throw new InvalidOperationException(
                    $"{run.Path} was created with system {Show(runManifest.SystemDescription)}; "
                    + $"cannot resume it with {Show(description)}.");
            runManifest = runManifest with { SystemDescription = description };
            run.WriteManifest(runManifest);

            foreach (string name in pending)
            {
                DatasetEntry entry = manifest.Datasets[name];
                EvalDataset dataset = await DatasetAdapters.Get(entry.Adapter)
                    .LoadAsync(name, entry, cache.DirectoryFor(name, entry), ct);
                log.WriteLine($"[{name}] indexing {dataset.Corpus.Count} documents");
                IndexReport index = await system.IndexAsync(dataset, ct);
                bool invalid = dataset.Corpus.Count > 0 && (double)index.Failed / dataset.Corpus.Count > MaxFailedFraction;
                if (invalid)
                    log.WriteLine($"[{name}] INVALID: {index.Failed}/{dataset.Corpus.Count} documents failed to ingest");

                IReadOnlyList<EvalQuery> queries = request.LimitQueries is int limit
                    ? dataset.Queries.Take(limit).ToList()
                    : dataset.Queries;
                List<QueryResult> results = [];
                foreach (EvalQuery query in queries)
                {
                    SearchOutcome outcome = await system.SearchAsync(name, query, K, ct);
                    results.Add(new QueryResult(name, query.Id, query.Text, query.Split, outcome.Ranked, outcome.Trace, outcome.Error));
                    if (results.Count % 50 == 0)
                        log.WriteLine($"[{name}] searched {results.Count}/{queries.Count}");
                }

                run.WriteDataset(name, dataset.Qrels, Titles(dataset, results), results);
                RunDatasetInfo info = new(name, entry.Version, hashes[name], entry.Tags,
                    dataset.Corpus.Count, index.Failed, invalid, queries.Count);
                runManifest = runManifest with { Datasets = [.. runManifest.Datasets.Where(d => d.Name != name), info] };
                run.WriteManifest(runManifest);
                run.MarkComplete(name);
            }
        }

        runManifest = runManifest with { FinishedUtc = DateTimeOffset.UtcNow };
        run.WriteManifest(runManifest);
        return run;
    }

    private (RunFolder Run, RunManifest Manifest) OpenOrCreate(RunRequest request, SystemConfig config)
    {
        (string sha, bool dirty) = GitInfo.Read(paths.RepoRoot);
        if (request.ResumeDir is not null)
        {
            RunFolder existing = RunFolder.Open(request.ResumeDir);
            RunManifest manifest = existing.ReadManifest();
            if (manifest.Suite != request.Suite || manifest.System != request.System
                || manifest.Config != request.Config || manifest.ConfigHash != config.Hash)
                throw new InvalidOperationException(
                    $"{request.ResumeDir} was created for {manifest.Suite}/{manifest.System}/{manifest.Config} "
                    + $"(config hash {manifest.ConfigHash}); cannot resume it as {request.Suite}/{request.System}/{request.Config} ({config.Hash}).");
            if (manifest.LimitQueries != request.LimitQueries)
                throw new InvalidOperationException(
                    $"{request.ResumeDir} was created with --limit-queries {Show(manifest.LimitQueries)}; "
                    + $"cannot resume it with --limit-queries {Show(request.LimitQueries)}.");

            // Older manifests have no resume list.
            IReadOnlyList<RunResume> resumes = manifest.Resumes ?? [];
            manifest = manifest with { Resumes = resumes };
            RunResume? last = resumes.Count > 0 ? resumes[^1] : null;
            if (sha != (last?.GitSha ?? manifest.GitSha) || dirty != (last?.GitDirty ?? manifest.GitDirty))
            {
                log.WriteLine($"WARNING: resuming a run started at git {manifest.GitSha}{(manifest.GitDirty ? " (dirty)" : "")} "
                    + $"from git {sha}{(dirty ? " (dirty)" : "")}; datasets scored before and after may come from different code.");
                manifest = manifest with { Resumes = [.. resumes, new RunResume(sha, dirty, DateTimeOffset.UtcNow)] };
                existing.WriteManifest(manifest);
            }
            return (existing, manifest);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        RunFolder run = RunFolder.Create(paths.RunsRoot, request.Suite, request.System, request.Config, sha, now);
        RunManifest created = new(sha, dirty, request.Suite, request.System, request.Config, config.Hash, config.SearchMode,
            config.Settings, new Dictionary<string, string>(), Environment.MachineName,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription, Environment.ProcessorCount, now, null, [],
            request.LimitQueries, []);
        run.WriteManifest(created);
        return (run, created);
    }

    private static bool SameDescription(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out string? value) && value == p.Value);

    private static string Show(IReadOnlyDictionary<string, string> description) =>
        string.Join(", ", description.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));

    private static string Show(int? limit) =>
        limit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none";

    /// <summary>Titles for documents the report can show: judged ones and returned ones.</summary>
    private static Dictionary<string, string> Titles(EvalDataset dataset, IReadOnlyList<QueryResult> results)
    {
        HashSet<string> wanted = new(StringComparer.Ordinal);
        foreach (QueryResult result in results)
        {
            wanted.UnionWith(dataset.Qrels.For(result.QueryId).Keys);
            wanted.UnionWith(result.Ranked.Select(r => r.DocId));
        }
        return dataset.Corpus
            .Where(d => wanted.Contains(d.Id))
            .ToDictionary(d => d.Id, d => d.Title ?? Truncate(d.Text ?? "", 80), StringComparer.Ordinal);
    }

    private static string Truncate(string text, int length) =>
        text.Length <= length ? text : text[..length] + "…";
}
