using Connapse.Eval.Checks;
using Connapse.Eval.Cli;
using Connapse.Eval.Datasets;
using Connapse.Eval.Model;
using Connapse.Eval.Systems;

namespace Connapse.Eval.Runs;

/// <param name="RealEmbedder">
/// Use the embedding provider the config names instead of the offline hashing embedder. The Semantic
/// chunker places boundaries by embedding similarity, so chunk-level results only match production
/// with the real model.
/// </param>
public sealed record ExtractRequest(string Suite, string Config, IReadOnlyList<string> OnlyDatasets, string? ResumeDir, bool RealEmbedder);

/// <summary>
/// Ingests each dataset's raw files through Connapse, reads back the parsed text and the stored
/// chunks, and writes a record per document and per check. A document that fails or stalls is a
/// finding, never a reason to stop the run.
/// </summary>
public sealed class ExtractRunner(
    RepoPaths paths,
    TextWriter log,
    HttpClient http,
    Func<SystemConfig, bool, CancellationToken, Task<ConnapseSearchSystem>> systemFactory,
    string? runsRoot = null)
{
    public const string SystemName = "connapse";

    public async Task<RunFolder> RunAsync(ExtractRequest request, CancellationToken ct)
    {
        EvalManifest manifest = EvalManifest.Load(paths.ManifestPath);
        IReadOnlyList<string> names = manifest.ResolveSuite(request.Suite, request.OnlyDatasets);
        foreach (string name in names)
            if (DatasetAdapters.Get(manifest.Datasets[name].Adapter) is not IExtractionAdapter)
                throw new ArgumentException($"Dataset '{name}' has no extraction checks; 'extract' runs extraction suites such as extract-v1.");

        DatasetCache cache = new(paths.CacheRoot, http, paths.DatasetsRoot);
        Dictionary<string, IReadOnlyDictionary<string, string>> hashes = new(StringComparer.Ordinal);
        foreach (string name in names)
            hashes[name] = await cache.EnsureAsync(name, manifest.Datasets[name], allowUnpinned: false, ct);

        SystemConfig config = SystemConfig.Load(paths.EvalRoot, SystemName, request.Config);
        (RunFolder run, RunManifest runManifest) = OpenOrCreate(request, config);

        List<string> pending = names.Where(n => !run.IsDatasetComplete(n)).ToList();
        EvalRunner.RefuseMixedDatasetRevisions(names.Except(pending), runManifest, manifest, hashes, run);
        if (pending.Count > 0)
        {
            await using ConnapseSearchSystem system = await systemFactory(config, request.RealEmbedder, ct);
            IReadOnlyDictionary<string, string> description = system.Describe();
            if (runManifest.SystemDescription.Count > 0 && !runManifest.SystemDescription.OrderBy(p => p.Key).SequenceEqual(description.OrderBy(p => p.Key)))
                throw new InvalidOperationException(
                    $"{run.Path} was started with a different system (for example a different embedder); cannot resume it with this one.");
            runManifest = runManifest with { SystemDescription = description };
            run.WriteManifest(runManifest);

            foreach (string name in pending)
            {
                DatasetEntry entry = manifest.Datasets[name];
                string directory = cache.DirectoryFor(name, entry);
                IExtractionAdapter adapter = (IExtractionAdapter)DatasetAdapters.Get(entry.Adapter);
                EvalDataset dataset = await adapter.LoadAsync(name, entry, directory, ct);
                ExtractionSpec spec = await adapter.LoadChecksAsync(directory, ct);
                string[] unexpected = dataset.Corpus.Where(d => !spec.Documents.ContainsKey(d.Id)).Select(d => d.Id).ToArray();
                if (unexpected.Length > 0)
                    throw new InvalidDataException($"{name}: no expectation for {string.Join(", ", unexpected.Take(5))}.");

                // Recorded before indexing: a dataset listed here with no .done marker did not finish,
                // which for an in-process host usually means ingestion crashed it.
                runManifest = runManifest with
                {
                    Datasets = [.. runManifest.Datasets.Where(d => d.Name != name),
                        new RunDatasetInfo(name, entry.Version, hashes[name], entry.Tags, dataset.Corpus.Count, 0, false, 0)],
                };
                run.WriteManifest(runManifest);

                log.WriteLine($"[{name}] ingesting {dataset.Corpus.Count} files, {spec.Checks.Count} checks");
                IndexReport index = await system.IndexAsync(dataset, IngestionWait.RecordStalls, ct);
                (List<DocumentRecord> documents, List<CheckRecord> checks) = await EvaluateAsync(system, name, dataset, spec, index, ct);

                run.WriteExtraction(name, documents, checks);
                runManifest = runManifest with
                {
                    Datasets = [.. runManifest.Datasets.Where(d => d.Name != name),
                        new RunDatasetInfo(name, entry.Version, hashes[name], entry.Tags, dataset.Corpus.Count, index.Failed, false, 0)],
                };
                run.WriteManifest(runManifest);
                run.MarkComplete(name);
                log.WriteLine($"[{name}] {checks.Count(c => c.Outcome == CheckOutcome.Pass)}/{checks.Count(c => c.Outcome != CheckOutcome.Skipped)} checks pass");
            }
        }

        runManifest = runManifest with { FinishedUtc = DateTimeOffset.UtcNow };
        run.WriteManifest(runManifest);
        return run;
    }

    private async Task<(List<DocumentRecord>, List<CheckRecord>)> EvaluateAsync(
        ConnapseSearchSystem system, string name, EvalDataset dataset, ExtractionSpec spec, IndexReport index, CancellationToken ct)
    {
        ILookup<string, ExtractionCheck> checksByDoc = spec.Checks.ToLookup(c => c.Test.Pdf, StringComparer.Ordinal);
        Dictionary<string, DocumentOutcome> outcomes = index.Outcomes.ToDictionary(o => o.DatasetDocId, StringComparer.Ordinal);
        List<DocumentRecord> documents = [];
        List<CheckRecord> checks = [];

        foreach (EvalDocument doc in dataset.Corpus)
        {
            DocumentOutcome outcome = outcomes[doc.Id];
            ProbeResult probe = await IngestionProbe.ProbeAsync(system.Services, doc.FilePath!,
                outcome.UploadError is null ? outcome.ConnapseDocId : null, ct);
            DocumentExpectation expectation = spec.Documents[doc.Id];
            DocumentRecord record = new(name, doc.Id, expectation.Category, expectation.Expected,
                outcome.UploadError, outcome.Status, outcome.ErrorMessage, outcome.IngestionStatus?.ToString(), outcome.Stalled,
                outcome.Elapsed.TotalMilliseconds, probe.ParsedText?.Length, probe.PageCount, probe.EmptyPages, probe.Chunks.Count,
                probe.ParserWarnings, probe.ParseError);
            documents.Add(record);
            checks.AddRange(ExtractionEvaluator.Evaluate(record, new DocumentTexts(probe.ParsedText, probe.Chunks), checksByDoc[doc.Id]));
            if (documents.Count % 200 == 0)
                log.WriteLine($"[{name}] checked {documents.Count}/{dataset.Corpus.Count}");
        }
        return (documents, checks);
    }

    private (RunFolder Run, RunManifest Manifest) OpenOrCreate(ExtractRequest request, SystemConfig config)
    {
        (string sha, bool dirty) = GitInfo.Read(paths.RepoRoot);
        if (request.ResumeDir is not null)
        {
            RunFolder existing = RunFolder.Open(request.ResumeDir);
            RunManifest manifest = existing.ReadManifest();
            if (manifest.Kind != RunManifest.ExtractKind || manifest.Suite != request.Suite
                || manifest.Config != request.Config || manifest.ConfigHash != config.Hash)
                throw new InvalidOperationException(
                    $"{request.ResumeDir} is not an extract run of {request.Suite}/{request.Config} ({config.Hash}); cannot resume it.");
            foreach (RunDatasetInfo started in manifest.Datasets.Where(d => !existing.IsDatasetComplete(d.Name)))
                log.WriteLine($"WARNING: {started.Name} was started but did not finish in the earlier attempt (the host may have crashed); retrying it.");
            return (existing, manifest);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        RunFolder run = RunFolder.Create(runsRoot ?? paths.RunsRoot, request.Suite, SystemName, request.Config, sha, now);
        RunManifest created = new(sha, dirty, request.Suite, SystemName, request.Config, config.Hash, config.SearchMode,
            config.Settings, new Dictionary<string, string>(), Environment.MachineName,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription, Environment.ProcessorCount, now, null, [],
            null, [], RunManifest.ExtractKind);
        run.WriteManifest(created);
        return (run, created);
    }
}
