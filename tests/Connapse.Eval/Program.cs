using System.Text.Json;
using Connapse.Eval.Cli;
using Connapse.Eval.Datasets;
using Connapse.Eval.Model;
using Connapse.Eval.Reports;
using Connapse.Eval.Runs;
using Connapse.Eval.Systems;

namespace Connapse.Eval;

internal static class EvalEntryPoint
{
    public static async Task<int> Main(string[] args)
    {
        // First, before anything touches Regex; the hosted Connapse instance runs in this process.
        Connapse.Core.Utilities.RegexTimeout.ApplyProcessDefault();
        CliArgs cli = CliArgs.Parse(args);
        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            cli.EnsureKnownOptions();
            RepoPaths paths = RepoPaths.Find(Directory.GetCurrentDirectory());
            using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(30) };
            return cli.Command switch
            {
                "run" => await Commands.RunAsync(cli, paths, http, cts.Token),
                "extract" => await Commands.ExtractAsync(cli, paths, http, cts.Token),
                "pool" => Commands.PoolUnjudged(cli),
                "datasets" => await Commands.DatasetsAsync(cli, paths, http, cts.Token),
                "compare" => Commands.Compare(cli),
                "vector-index" => cli.Option("scale") is string scale
                    ? await new VectorIndexScaleProbe(paths, Console.Out).RunAsync(scale, cli.PositiveInt("limit-queries") ?? 50, cli.Flag("strategy"), cli.Flag("insert-bench"), cts.Token)
                    : await new VectorIndexProbe(paths, Console.Out, http)
                        .RunAsync(cli.Required("suite"), cli.List("datasets"), cli.PositiveInt("limit-queries"), cts.Token),
                "report" => Commands.Report(cli),
                _ => Commands.Usage(),
            };
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.Error.WriteLine("cancelled");
            return 130;
        }
        catch (OperationCanceledException ex)
        {
            // HttpClient reports its timeout as a TaskCanceledException wrapping a TimeoutException.
            Console.Error.WriteLine($"error: timed out: {ex.InnerException?.Message ?? ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }
}

internal static class Commands
{
    public static async Task<int> RunAsync(CliArgs cli, RepoPaths paths, HttpClient http, CancellationToken ct)
    {
        IReadOnlyList<string> configs = cli.List("config");
        if (configs.Count == 0)
            throw new ArgumentException("--config is required.");
        string system = cli.Option("system") ?? "connapse";
        if (system != "connapse")
            throw new ArgumentException($"Unknown system '{system}'. Known: connapse.");

        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        EvalRunner runner = new(paths, Console.Out, http, async (config, token) =>
            await ConnapseSearchSystem.StartAsync(config, paths.WebContentRoot, embeddings, Console.Out, null, token));

        // Several configs share an index wherever their index-time settings match (#667).
        if (configs.Count > 1)
        {
            if (cli.Option("resume") is not null)
                throw new ArgumentException("--resume takes one config; resume each run folder on its own.");
            IReadOnlyList<RunFolder> runs = await runner.RunManyAsync(new MultiRunRequest(
                cli.Required("suite"), system, configs, cli.List("datasets"), cli.PositiveInt("limit-queries")), ct);
            int exit = 0;
            foreach (RunFolder each in runs)
                exit = Math.Max(exit, WriteScores(each));
            return exit;
        }

        RunFolder run = await runner.RunAsync(new RunRequest(cli.Required("suite"), system, configs[0],
            cli.List("datasets"), cli.Option("resume"), cli.PositiveInt("limit-queries")), ct);
        return WriteScores(run);
    }

    /// <summary>Writes the run's report and prints its scores; 1 when a dataset is invalid.</summary>
    private static int WriteScores(RunFolder run)
    {
        RunScores scores = Scoring.Score(run);
        ReportWriter.Write(run, scores);
        Console.WriteLine(run.Path);
        foreach (DatasetScores d in scores.Datasets)
            Console.WriteLine(d.Invalid
                ? $"  {d.Name,-28} not scored: {d.NotScoredReason}"
                : d.PerQuery.Count == 0
                ? $"  {d.Name,-28} no scored test queries"
                : $"  {d.Name,-28} nDCG@10 {d.Means["nDCG@10"]:F3}  MRR@10 {d.Means["MRR@10"]:F3}  judged@10 {d.Means["judged@10"]:F2}"
                    + string.Concat(d.Kinds.Select(k => $"\n    {k.Kind,-26} nDCG@10 {k.Means["nDCG@10"]:F3}  Recall@10 {k.Means["Recall@10"]:F3}  (n={k.Queries})")));
        Console.WriteLine($"  {"portfolio",-28} nDCG@10 {scores.Portfolio["nDCG@10"]:F3}");
        return scores.Datasets.Any(d => d.Invalid) ? 1 : 0;
    }

    public static async Task<int> ExtractAsync(CliArgs cli, RepoPaths paths, HttpClient http, CancellationToken ct)
    {
        ExtractRequest request = new(cli.Required("suite"), cli.Option("config") ?? "extract", cli.List("datasets"),
            cli.Option("resume"), cli.Flag("real-embedder"));
        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        ExtractRunner runner = new(paths, Console.Out, http, async (config, realEmbedder, token) =>
            await ConnapseSearchSystem.StartAsync(config, paths.WebContentRoot, embeddings, Console.Out,
                realEmbedder ? null : new HashingEmbeddingProvider(), token));
        RunFolder run = await runner.RunAsync(request, ct);

        ExtractionScores scores = ExtractionScoring.Score(run);
        run.WriteText("report.html", HtmlReport.RenderExtraction(scores, run));
        Console.WriteLine(run.Path);
        Console.WriteLine($"  silent-failure rate      {Percent(scores.SilentFailureRate)}");
        Console.WriteLine($"  fails-loudly pass rate   {Percent(scores.FailsLoudlyRate)}");
        foreach ((string level, double value) in scores.OlmOcrNative)
            Console.WriteLine($"  olmOCR native ({level,-7})  {Percent(value)}");
        if (scores.OlmOcrComparable is double comparable)
            Console.WriteLine($"  olmOCR comparable        {Percent(comparable)}");
        foreach (ExtractionDatasetScore d in scores.Datasets.Where(d => !d.Complete))
            Console.WriteLine($"  {d.Name} did not finish");
        return scores.Datasets.Any(d => !d.Complete) ? 1 : 0;
    }

    private static string Percent(double value) =>
        double.IsNaN(value) ? "—" : (value * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";

    public static int PoolUnjudged(CliArgs cli)
    {
        if (cli.Positionals.Count == 0)
            throw new ArgumentException("pool needs at least one run folder.");
        IReadOnlyList<PoolItem> items = Pool.Unjudged(cli.Positionals.Select(RunFolder.Open));
        string output = cli.Required("out");
        File.WriteAllLines(output, items.Select(i => JsonSerializer.Serialize(i, EvalJson.Line)));
        Console.WriteLine($"{items.Count} unjudged (query, document) pairs written to {output}");
        return 0;
    }

    public static async Task<int> DatasetsAsync(CliArgs cli, RepoPaths paths, HttpClient http, CancellationToken ct)
    {
        EvalManifest manifest = EvalManifest.Load(paths.ManifestPath);
        string action = cli.Positionals.FirstOrDefault() ?? "list";
        if (action == "list")
        {
            foreach ((string suite, IReadOnlyList<string> names) in manifest.Suites)
                Console.WriteLine($"{suite}: {string.Join(", ", names)}");
            return 0;
        }

        if (action is not ("verify" or "fetch" or "pin"))
            throw new ArgumentException($"Unknown datasets action '{action}'. Use list, verify, fetch or pin.");

        IReadOnlyList<string> datasets = manifest.ResolveSuite(cli.Required("suite"), cli.List("datasets"));
        DatasetCache cache = new(paths.CacheRoot, http, paths.DatasetsRoot);
        foreach (string name in datasets)
        {
            IReadOnlyDictionary<string, string> hashes =
                await cache.EnsureAsync(name, manifest.Datasets[name], allowUnpinned: action == "pin", ct);
            if (action == "pin")
                manifest = manifest.WithPinnedHashes(name, hashes);
            Console.WriteLine($"{name}: ok");
        }
        if (action == "pin")
            manifest.Save(paths.ManifestPath);
        return 0;
    }

    public static int Compare(CliArgs cli)
    {
        if (cli.Positionals.Count != 2)
            throw new ArgumentException("compare needs exactly two run folders: <baseline> <candidate>.");
        RunFolder baseline = RunFolder.Open(cli.Positionals[0]);
        RunFolder candidate = RunFolder.Open(cli.Positionals[1]);
        if (baseline.IsExtraction != candidate.IsExtraction)
            throw new ArgumentException("compare needs two ranking runs or two extract runs, not one of each.");
        if (baseline.IsExtraction)
        {
            ExtractionComparisonResult extraction = ExtractionComparisonBuilder.Build(baseline, candidate);
            string extractionStem = $"compare-vs-{baseline.Name}";
            candidate.WriteText(extractionStem + ".html", HtmlReport.RenderExtractionComparison(extraction));
            candidate.WriteText(extractionStem + ".json", JsonSerializer.Serialize(extraction, EvalJson.Options));
            Console.WriteLine(extraction.Verdict);
            Console.WriteLine(Path.Combine(candidate.Path, extractionStem + ".html"));
            return 0;
        }
        Comparison comparison = ComparisonBuilder.Build(
            Scoring.Score(baseline), Scoring.Score(candidate), cli.Flag("allow-dataset-mismatch"));
        string stem = $"compare-vs-{baseline.Name}";
        candidate.WriteText(stem + ".html", HtmlReport.RenderComparison(comparison));
        candidate.WriteText(stem + ".json", JsonSerializer.Serialize(comparison, EvalJson.Options));
        Console.WriteLine(comparison.Verdict);
        Console.WriteLine(Path.Combine(candidate.Path, stem + ".html"));
        return 0;
    }

    public static int Report(CliArgs cli)
    {
        RunFolder run = RunFolder.Open(cli.Positionals.FirstOrDefault()
            ?? throw new ArgumentException("report needs a run folder."));
        ReportWriter.Write(run, Scoring.Score(run));
        Console.WriteLine(Path.Combine(run.Path, "report.html"));
        return 0;
    }

    public static int Usage()
    {
        Console.Error.WriteLine("""
            usage: dotnet run --project tests/Connapse.Eval -- <command>
              run      --suite <name> --config <name>[,<name>...] [--system connapse] [--datasets a,b] [--resume <runDir>] [--limit-queries N]
                       several configs index once per group with the same index-time settings
              extract  --suite <name> [--config extract] [--datasets a,b] [--resume <runDir>] [--real-embedder]
              compare  <runDirA> <runDirB> [--allow-dataset-mismatch]
              vector-index --suite <name> [--datasets a,b] [--limit-queries N]   (production index vs exact search)
              vector-index --scale <dir> [--limit-queries N] [--strategy] [--insert-bench]
              report   <runDir>
              pool     <runDir>... --out <file>
              datasets list | verify | fetch | pin --suite <name> [--datasets a,b]
            """);
        return 2;
    }
}

internal static class ReportWriter
{
    public static void Write(RunFolder run, RunScores scores)
    {
        run.WriteText("report.html", HtmlReport.RenderRun(scores, run));
        run.WriteText("report.json", JsonSerializer.Serialize(scores, EvalJson.Options));
    }
}
