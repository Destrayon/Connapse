using Connapse.Eval.Cli;
using Connapse.Eval.Datasets;
using Connapse.Eval.Metrics;
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

/// <summary>Several configs over one suite, sharing an index wherever their index-time settings match (#667).</summary>
public sealed record MultiRunRequest(
    string Suite,
    string System,
    IReadOnlyList<string> Configs,
    IReadOnlyList<string> OnlyDatasets,
    int? LimitQueries);

public sealed class EvalRunner(
    RepoPaths paths,
    TextWriter log,
    HttpClient http,
    Func<SystemStart, CancellationToken, Task<ISystemUnderTest>> systemFactory)
{
    public const int K = 10;
    private const double MaxFailedFraction = 0.01;

    /// <summary>
    /// Restore an index cached by an earlier run with the same key instead of indexing, and cache the
    /// index a run builds (#672). Only a run that indexes every dataset of its suite uses the cache.
    /// </summary>
    public bool UseIndexCache { get; init; }

    public async Task<RunFolder> RunAsync(RunRequest request, CancellationToken ct)
    {
        (EvalManifest manifest, IReadOnlyList<string> names, DatasetCache cache,
            Dictionary<string, IReadOnlyDictionary<string, string>> hashes) =
            await PrepareAsync(request.Suite, request.OnlyDatasets, request.LimitQueries, ct);

        SystemConfig config = SystemConfig.Load(paths.EvalRoot, request.System, request.Config);
        (RunFolder run, RunManifest runManifest) = OpenOrCreate(request, config);

        List<string> pending = names.Where(n => !run.IsDatasetComplete(n)).ToList();
        RefuseMixedDatasetRevisions(names.Except(pending), runManifest, manifest, hashes, run);
        if (pending.Count > 0)
        {
            string? cacheKey = pending.Count == names.Count ? CacheKey(config, names, manifest, hashes) : null;
            await using ISystemUnderTest system = await systemFactory(new SystemStart(config, cacheKey), ct);
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
                IndexReport index = await IndexAsync(system, name, dataset, ct);
                runManifest = await SearchDatasetAsync(system, name, entry, dataset, index, hashes[name],
                    request.LimitQueries, run, runManifest, ct);
            }
            runManifest = runManifest with { IndexCache = await FinishIndexCacheAsync(system, cacheKey, pending, ct) };
        }

        runManifest = runManifest with { FinishedUtc = DateTimeOffset.UtcNow };
        run.WriteManifest(runManifest);
        return run;
    }

    /// <summary>
    /// Runs every config over the suite, indexing each dataset once per group of configs whose
    /// index-time settings match (<see cref="SystemConfig.IndexKey"/>), then searching that index under
    /// each config in turn (#667). Each config gets its own run folder, as a separate <see cref="RunAsync"/>
    /// would produce. Resuming is not supported: a run folder is resumed with <see cref="RunAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<RunFolder>> RunManyAsync(MultiRunRequest request, CancellationToken ct)
    {
        if (request.Configs.Count == 0)
            throw new ArgumentException("Give at least one config.");
        string[] repeated = request.Configs.GroupBy(c => c, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        if (repeated.Length > 0)
            throw new ArgumentException($"Config listed more than once: {string.Join(", ", repeated)}.");

        (EvalManifest manifest, IReadOnlyList<string> names, DatasetCache cache,
            Dictionary<string, IReadOnlyDictionary<string, string>> hashes) =
            await PrepareAsync(request.Suite, request.OnlyDatasets, request.LimitQueries, ct);

        List<(SystemConfig Config, RunFolder Run, RunManifest Manifest)> runs = [];
        foreach (string name in request.Configs)
        {
            SystemConfig config = SystemConfig.Load(paths.EvalRoot, request.System, name);
            (RunFolder run, RunManifest runManifest) = OpenOrCreate(
                new RunRequest(request.Suite, request.System, name, request.OnlyDatasets, null, request.LimitQueries), config);
            runs.Add((config, run, runManifest));
        }

        foreach (IGrouping<string, int> group in Enumerable.Range(0, runs.Count).GroupBy(i => runs[i].Config.IndexKey))
        {
            int[] members = group.ToArray();
            log.WriteLine($"index shared by: {string.Join(", ", members.Select(i => runs[i].Config.Name))}");
            SystemConfig indexConfig = runs[members[0]].Config.IndexOnly();
            string? cacheKey = CacheKey(indexConfig, names, manifest, hashes);
            await using ISystemUnderTest system = await systemFactory(new SystemStart(indexConfig, cacheKey), ct);
            foreach (string name in names)
            {
                DatasetEntry entry = manifest.Datasets[name];
                EvalDataset dataset = await DatasetAdapters.Get(entry.Adapter)
                    .LoadAsync(name, entry, cache.DirectoryFor(name, entry), ct);
                IndexReport index = await IndexAsync(system, name, dataset, ct);
                foreach (int i in members)
                {
                    (SystemConfig config, RunFolder run, RunManifest runManifest) = runs[i];
                    await system.UseSearchConfigAsync(config, ct);
                    IReadOnlyDictionary<string, string> description = system.Describe();
                    if (runManifest.SystemDescription.Count > 0 && !SameDescription(runManifest.SystemDescription, description))
                        throw new InvalidOperationException(
                            $"{config.Name}'s system changed between datasets: {Show(runManifest.SystemDescription)} became {Show(description)}.");
                    runManifest = runManifest with { SystemDescription = description };
                    // Written before searching, so a folder resumed after a failed search is still checked against it.
                    run.WriteManifest(runManifest);
                    log.WriteLine($"[{name}] searching under {config.Name}");
                    runs[i] = (config, run, await SearchDatasetAsync(system, name, entry, dataset, index, hashes[name],
                        request.LimitQueries, run, runManifest, ct));
                }
            }
            string? indexCache = await FinishIndexCacheAsync(system, cacheKey, names, ct);
            foreach (int i in members)
                runs[i] = runs[i] with { Manifest = runs[i].Manifest with { IndexCache = indexCache } };
        }

        List<RunFolder> folders = [];
        foreach ((_, RunFolder run, RunManifest runManifest) in runs)
        {
            run.WriteManifest(runManifest with { FinishedUtc = DateTimeOffset.UtcNow });
            folders.Add(run);
        }
        return folders;
    }

    /// <summary>Indexes a dataset, or takes it from the system's restored index cache.</summary>
    private async Task<IndexReport> IndexAsync(ISystemUnderTest system, string name, EvalDataset dataset, CancellationToken ct)
    {
        if ((system as IIndexCachingSystem)?.RestoredIndex(name) is IndexReport restored)
        {
            log.WriteLine($"[{name}] index restored from the cache");
            return restored;
        }
        log.WriteLine($"[{name}] indexing {dataset.Corpus.Count} documents");
        return await system.IndexAsync(dataset, ct);
    }

    private string? CacheKey(SystemConfig config, IReadOnlyList<string> names, EvalManifest manifest,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> hashes) =>
        UseIndexCache
            ? IndexCacheKey.Compute(config, names.Select(n => (n, manifest.Datasets[n].Version, hashes[n])),
                IndexCacheKey.SourceHash(paths.RepoRoot), IndexCacheKey.CurrentEnvironment())
            : null;

    /// <summary>Saves the index the system built under the cache key; what the manifest records about the cache.</summary>
    private static async Task<string?> FinishIndexCacheAsync(
        ISystemUnderTest system, string? cacheKey, IReadOnlyList<string> names, CancellationToken ct)
    {
        if (cacheKey is null || system is not IIndexCachingSystem caching)
            return null;
        if (caching.Restored)
            return $"restored {cacheKey}";
        await caching.SaveIndexCacheAsync(names, ct);
        return $"saved {cacheKey}";
    }

    /// <summary>Resolves the suite and verifies every dataset file before any container starts, so a checksum problem fails in seconds.</summary>
    private async Task<(EvalManifest Manifest, IReadOnlyList<string> Names, DatasetCache Cache,
        Dictionary<string, IReadOnlyDictionary<string, string>> Hashes)> PrepareAsync(
        string suite, IReadOnlyList<string> onlyDatasets, int? limitQueries, CancellationToken ct)
    {
        if (limitQueries is < 1)
            throw new ArgumentException($"--limit-queries must be at least 1 (got {limitQueries}).");

        EvalManifest manifest = EvalManifest.Load(paths.ManifestPath);
        IReadOnlyList<string> names = manifest.ResolveSuite(suite, onlyDatasets);
        // Extraction datasets have no queries or relevance judgments; ranking them would index files,
        // score nothing, and count expected failures against the dataset.
        string[] extraction = names.Where(n => DatasetAdapters.Get(manifest.Datasets[n].Adapter) is IExtractionAdapter).ToArray();
        if (extraction.Length > 0)
            throw new ArgumentException(
                $"{string.Join(", ", extraction)} {(extraction.Length == 1 ? "is an extraction dataset" : "are extraction datasets")}; "
                + "score it with the 'extract' command, not 'run'.");
        DatasetCache cache = new(paths.CacheRoot, http, paths.DatasetsRoot);

        Dictionary<string, IReadOnlyDictionary<string, string>> hashes = new(StringComparer.Ordinal);
        foreach (string name in names)
            hashes[name] = await cache.EnsureAsync(name, manifest.Datasets[name], allowUnpinned: false, ct);
        return (manifest, names, cache, hashes);
    }

    /// <summary>Searches one indexed dataset under the system's current config and writes it to the run.</summary>
    private async Task<RunManifest> SearchDatasetAsync(
        ISystemUnderTest system, string name, DatasetEntry entry, EvalDataset dataset, IndexReport index,
        IReadOnlyDictionary<string, string> fileHashes, int? limitQueries, RunFolder run, RunManifest runManifest,
        CancellationToken ct)
    {
        bool invalid = dataset.Corpus.Count > 0 && (double)index.Failed / dataset.Corpus.Count > MaxFailedFraction;
        if (invalid)
            log.WriteLine($"[{name}] INVALID: {index.Failed}/{dataset.Corpus.Count} documents failed to ingest");

        IReadOnlyList<EvalQuery> queries = limitQueries is int limit
            ? LimitPerSplit(dataset.Queries, limit)
            : dataset.Queries;
        List<QueryResult> results = [];
        List<(string QueryId, CandidateCapture Candidates)> captured = [];
        // Passage judgments depend on what this run's parser and chunker produced, so they
        // are made here and written as the run's qrels.
        Qrels qrels = dataset.Passages is null ? dataset.Qrels : new Qrels();
        foreach (EvalQuery query in queries)
        {
            SearchOutcome outcome = await system.SearchAsync(name, query, K, ct);
            if (dataset.Passages is { } passages)
            {
                IReadOnlyList<RetrievedPassage> retrieved = outcome.Error is null ? outcome.Passages ?? [] : [];
                (IReadOnlyList<RankedDoc> ranked, IReadOnlyDictionary<string, int> judgments) =
                    PassageJudge.Judge(passages[query.Id], retrieved);
                foreach ((string passageId, int grade) in judgments)
                    qrels.Add(query.Id, passageId, grade);
                results.Add(new QueryResult(name, query.Id, query.Text, query.Split, ranked, outcome.Trace, outcome.Error, retrieved, query.Tags));
            }
            else
            {
                results.Add(new QueryResult(name, query.Id, query.Text, query.Split, outcome.Ranked, outcome.Trace, outcome.Error,
                    Tags: query.Tags.Count > 0 ? query.Tags : null));
            }
            if (outcome.Candidates is not null)
                captured.Add((query.Id, outcome.Candidates));
            if (results.Count % 50 == 0)
                log.WriteLine($"[{name}] searched {results.Count}/{queries.Count}");
        }

        run.WriteDataset(name, qrels, Titles(dataset, qrels, results), results);
        if (captured.Count > 0)
            run.WriteCandidates(name, captured);
        RunDatasetInfo info = new(name, entry.Version, fileHashes, entry.Tags,
            dataset.Corpus.Count, index.Failed, invalid, queries.Count);
        runManifest = runManifest with { Datasets = [.. runManifest.Datasets.Where(d => d.Name != name), info] };
        run.WriteManifest(runManifest);
        run.MarkComplete(name);
        return runManifest;
    }

    /// <summary>
    /// On resume, a dataset already marked complete in <paramref name="run"/> must still match the
    /// version and file hashes the current manifest just verified — otherwise the run would mix
    /// results scored against one dataset revision with results scored against another.
    /// </summary>
    internal static void RefuseMixedDatasetRevisions(
        IEnumerable<string> completed, RunManifest runManifest, EvalManifest manifest,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> hashes, RunFolder run)
    {
        List<string> mismatched = [];
        foreach (string name in completed)
        {
            RunDatasetInfo? recorded = runManifest.Datasets.FirstOrDefault(d => d.Name == name);
            if (recorded is null)
                continue;
            DatasetEntry current = manifest.Datasets[name];
            IReadOnlyDictionary<string, string> currentHashes = hashes[name];
            bool sameHashes = recorded.FileSha256.Count == currentHashes.Count
                && recorded.FileSha256.All(p => currentHashes.TryGetValue(p.Key, out string? h) && h == p.Value);
            if (recorded.Version != current.Version || !sameHashes)
                mismatched.Add($"{name} (run has version {recorded.Version}, manifest now has {current.Version})");
        }
        if (mismatched.Count > 0)
            throw new InvalidOperationException(
                $"{run.Path} would mix dataset revisions on resume: {string.Join(", ", mismatched)}.");
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

    /// <summary>Up to <paramref name="limit"/> queries of each split, keeping source order.</summary>
    public static IReadOnlyList<EvalQuery> LimitPerSplit(IReadOnlyList<EvalQuery> queries, int limit)
    {
        Dictionary<Split, int> taken = [];
        List<EvalQuery> result = [];
        foreach (EvalQuery query in queries)
        {
            int count = taken.GetValueOrDefault(query.Split);
            if (count >= limit)
                continue;
            taken[query.Split] = count + 1;
            result.Add(query);
        }
        return result;
    }

    private static bool SameDescription(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out string? value) && value == p.Value);

    private static string Show(IReadOnlyDictionary<string, string> description) =>
        string.Join(", ", description.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));

    private static string Show(int? limit) =>
        limit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none";

    /// <summary>Titles for documents the report can show: judged ones and returned ones.</summary>
    /// <remarks>Passage IDs take their document's title.</remarks>
    private static Dictionary<string, string> Titles(EvalDataset dataset, Qrels qrels, IReadOnlyList<QueryResult> results)
    {
        HashSet<string> wanted = new(StringComparer.Ordinal);
        foreach (QueryResult result in results)
        {
            wanted.UnionWith(qrels.For(result.QueryId).Keys);
            wanted.UnionWith(result.Ranked.Select(r => r.DocId));
        }
        Dictionary<string, string> titles = dataset.Corpus
            .ToDictionary(d => d.Id, d => d.Title ?? Truncate(d.Text ?? Path.GetFileName(d.FilePath) ?? d.Id, 80), StringComparer.Ordinal);
        return wanted
            .Select(id => (Id: id, Doc: dataset.Passages is null ? id : PassageJudge.DocumentOf(id)))
            .Where(x => titles.ContainsKey(x.Doc))
            .ToDictionary(x => x.Id, x => titles[x.Doc], StringComparer.Ordinal);
    }

    private static string Truncate(string text, int length) =>
        text.Length <= length ? text : text[..length] + "…";
}
