using System.Text.Json;
using System.Text.Json.Nodes;
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
        if (cli.WantsHelp)
            return Commands.Help();
        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            cli.EnsureKnownOptions();
            RepoPaths paths = RepoPaths.Find(Directory.GetCurrentDirectory());
            using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(30) };
            // Some dataset hosts (archives.gov) answer a request with no User-Agent with 404.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Connapse-Eval/1.0 (+https://github.com/Destrayon/Connapse)");
            return cli.Command switch
            {
                "run" => await Commands.RunAsync(cli, paths, http, cts.Token),
                "extract" => await Commands.ExtractAsync(cli, paths, http, cts.Token),
                "pool" => Commands.PoolUnjudged(cli),
                "datasets" => await Commands.DatasetsAsync(cli, paths, http, cts.Token),
                "compare" => Commands.Compare(cli),
                "tables-compare" => Commands.TablesCompare(cli),
                "vector-index" => cli.Option("scale") is string scale
                    ? await new VectorIndexScaleProbe(paths, Console.Out).RunAsync(scale, cli.PositiveInt("limit-queries") ?? 50, cli.Flag("strategy"), cli.Flag("insert-bench"), cts.Token)
                    : await new VectorIndexProbe(paths, Console.Out, http)
                        .RunAsync(cli.Required("suite"), cli.List("datasets"), cli.PositiveInt("limit-queries"), cts.Token),
                "report" => Commands.Report(cli),
                "memory" => await new MemoryEval(paths, http, Console.Out).RunAsync(
                    cli.Option("config") ?? "hybrid", cli.Option("split") ?? "heldout", cli.Option("out"), cts.Token),
                "memory-search" => await new MemorySearchEval(paths, http, Console.Out).RunAsync(new MemorySearchEval.Options(
                    cli.Option("config") ?? "hybrid", cli.Option("split") ?? "test", cli.Option("model") ?? "claude-sonnet-5-5",
                    cli.Option("effort") ?? "medium", cli.PositiveInt("repetitions") ?? 3, cli.PositiveInt("limit-tasks"),
                    cli.PositiveInt("workers") ?? 1, cli.Option("out"), cli.Option("agent-dir")), cts.Token),
                "tables-rag" => await new TablesRagEval(paths, http, Console.Out).RunAsync(new TablesRagEval.Options(
                    cli.Option("config") ?? "hybrid", cli.Option("split") ?? "test", cli.Option("model") ?? "claude-sonnet-5-5",
                    cli.Option("effort") ?? "medium", cli.PositiveInt("limit-tasks"), cli.List("ids"), cli.Flag("skip-mixed"),
                    cli.PositiveInt("workers") ?? 1, cli.Option("out"), cli.Option("agent-dir"), cli.List("tables"),
                    cli.PositiveInt("stall-minutes"), cli.Option("setup") ?? TablesRagEval.RagSetup), cts.Token),
                "tables-arms" => await new TablesArmsEval(paths, http, Console.Out).RunAsync(new TablesArmsEval.Options(
                    new TablesRagEval.Options(
                        cli.Option("config") ?? "hybrid", cli.Option("split") ?? "test", cli.Option("model") ?? "claude-sonnet-5-5",
                        cli.Option("effort") ?? "medium", cli.PositiveInt("limit-tasks"), cli.List("ids"), SkipMixed: true,
                        cli.PositiveInt("workers") ?? 1, cli.Option("out"), cli.Option("agent-dir"), cli.List("tables"),
                        cli.PositiveInt("stall-minutes")),
                    TableArm.Parse(cli.List("arms")), cli.PositiveInt("runs") ?? TablesArmsEval.RegisteredRuns,
                    cli.Option("set") ?? TablesArmsEval.TablesV1Set,
                    cli.PositiveInt("min-free-gb") is int armsGb ? armsGb * 1024L * 1024 * 1024 : TablesLinkedEval.DefaultMinFreeMemory), cts.Token),
                "tables-linked" => await new TablesLinkedEval(paths, Console.Out).RunAsync(new TablesLinkedEval.Options(
                    cli.Option("config") ?? "hybrid", cli.Option("model") ?? "claude-sonnet-5-5", cli.Option("effort") ?? "medium",
                    cli.PositiveInt("limit-tasks"), cli.List("ids"), cli.PositiveInt("workers") ?? 1, cli.Option("out"),
                    cli.Option("agent-dir"), cli.PositiveInt("stall-minutes"), cli.Flag("reserve"),
                    cli.PositiveInt("min-free-gb") is int gb ? gb * 1024L * 1024 * 1024 : TablesLinkedEval.DefaultMinFreeMemory), cts.Token),
                "tables-nested" => await new TablesNestedEval(paths, Console.Out).RunAsync(new TablesNestedEval.Options(
                    cli.Option("config") ?? "hybrid", cli.Option("split") ?? "test", cli.Option("model") ?? "claude-sonnet-5-5",
                    cli.Option("effort") ?? "medium", cli.PositiveInt("limit-tasks"), cli.List("ids"), cli.PositiveInt("workers") ?? 1,
                    cli.Option("out"), cli.Option("agent-dir"), cli.PositiveInt("stall-minutes"),
                    cli.PositiveInt("min-free-gb") is int nestedGb ? nestedGb * 1024L * 1024 * 1024 : TablesLinkedEval.DefaultMinFreeMemory), cts.Token),
                "tables-arms-score" => await TablesArmsEval.ScoreAsync(new TablesArmsEval.Options(
                    new TablesRagEval.Options(
                        cli.Option("config") ?? "hybrid", cli.Option("split") ?? "test", cli.Option("model") ?? "claude-sonnet-5-5",
                        cli.Option("effort") ?? "medium", cli.PositiveInt("limit-tasks"), cli.List("ids"), SkipMixed: true,
                        1, cli.Option("out"), Tables: cli.List("tables")),
                    TableArm.Parse(cli.List("arms")), cli.PositiveInt("runs") ?? TablesArmsEval.RegisteredRuns,
                    cli.Option("set") ?? TablesArmsEval.TablesV1Set),
                    cli.Required("runs-file"), Console.Out, cts.Token),
                "memory-learned" => await new MemoryLearnedEval(paths, http, Console.Out).RunAsync(new MemorySearchEval.Options(
                    cli.Option("config") ?? "hybrid", cli.Option("split") ?? "test", cli.Option("model") ?? "claude-sonnet-5-5",
                    cli.Option("effort") ?? "medium", cli.PositiveInt("repetitions") ?? 3, cli.PositiveInt("limit-tasks"),
                    cli.PositiveInt("workers") ?? 1, cli.Option("out"), cli.Option("agent-dir")), cts.Token),
                "code" => await new CodeEval(paths, http, Console.Out).RunAsync(
                    cli.Option("config") ?? "code", cli.Option("split") ?? "heldout", cli.Option("out"), cts.Token),
                "table-search" => await new TableSearchEval(paths, http, Console.Out).RunAsync(
                    cli.Option("config") ?? "hybrid", cli.Option("split") ?? "test", cli.Option("out"), cts.Token),
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

        string indexCache = cli.Option("index-cache") ?? "on";
        if (indexCache is not ("on" or "off"))
            throw new ArgumentException($"--index-cache takes on or off (got '{indexCache}').");

        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        EvalRunner runner = new(paths, Console.Out, http, async (start, token) =>
            await ConnapseSearchSystem.StartAsync(start.Config, paths.WebContentRoot, embeddings, Console.Out, null, token,
                start.IndexCacheKey is string key ? new IndexCacheStore(Path.Combine(paths.CacheRoot, "index-snapshots"), key) : null))
        {
            UseIndexCache = indexCache == "on",
        };

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

    public static int TablesCompare(CliArgs cli)
    {
        if (cli.Positionals.Count != 2)
            throw new ArgumentException("tables-compare needs exactly two runs files: <rag.runs.jsonl> <tables.runs.jsonl>.");
        JsonObject result = TablesProof.Compare(GradedRun.ReadAll(cli.Positionals[0]), GradedRun.ReadAll(cli.Positionals[1]));
        string json = result.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (cli.Option("out") is string output)
            File.WriteAllText(output, json + Environment.NewLine);
        Console.WriteLine(json);
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

    /// <summary>Asked-for help goes to stdout and succeeds; <see cref="Usage"/> is the error for a bad command.</summary>
    public static int Help()
    {
        Console.WriteLine(UsageText);
        return 0;
    }

    public static int Usage()
    {
        Console.Error.WriteLine(UsageText);
        return 2;
    }

    private const string UsageText = """
            usage: dotnet run --project tests/Connapse.Eval -- <command>
              run      --suite <name> --config <name>[,<name>...] [--system connapse] [--datasets a,b] [--resume <runDir>] [--limit-queries N] [--index-cache on|off]
                       several configs index once per group with the same index-time settings
              extract  --suite <name> [--config extract] [--datasets a,b] [--resume <runDir>] [--real-embedder]
              compare  <runDirA> <runDirB> [--allow-dataset-mismatch]
              vector-index --suite <name> [--datasets a,b] [--limit-queries N]   (production index vs exact search)
              vector-index --scale <dir> [--limit-queries N] [--strategy] [--insert-bench]
              report   <runDir>
              pool     <runDir>... --out <file>
              datasets list | verify | fetch | pin --suite <name> [--datasets a,b]
              memory   [--config hybrid] [--split heldout|tuning] [--out <file>]   (memory-v1: agent memory against notes as documents)
              memory-search [--config hybrid] [--split test|dev] [--model claude-sonnet-5-5] [--effort medium] [--repetitions 3] [--limit-tasks N] [--workers 1] [--agent-dir <dir>] [--out <file>]   (memory-search-v1: an agent answering with search alone; runs `claude -p` on your Claude subscription)
              memory-learned [same options as memory-search; --limit-tasks counts families]   (memory-learned-v1: an agent answering the session-2 questions with search alone, after the late documents)
              tables-rag [--config hybrid] [--split test|tune] [--model claude-sonnet-5-5] [--effort medium] [--ids a,b] [--tables a,b] [--limit-tasks N] [--skip-mixed] [--workers 1] [--agent-dir <dir>] [--stall-minutes 30] [--setup rag|tables] [--out <file>]   (tables-v1: an agent answering table questions with search tools (rag) or the four table tools (tables, mixed questions get all six); runs `claude -p` on your Claude subscription)
              tables-compare <rag.runs.jsonl> <tables.runs.jsonl> [--out <file>]   (scores a tables run against the rag baseline on the CON-E6 pass bar: McNemar, per family, mixed, cost)
              tables-arms [--arms structured,sql,graphql] [--runs 1] [--set tables-v1|flex] [--min-free-gb 8] [--config hybrid] [--split test|tune] [--model claude-sonnet-5-5] [--effort medium] [--ids a,b] [--tables a,b] [--limit-tasks N] [--workers 1] [--agent-dir <dir>] [--stall-minutes 30] [--out <file>]   (CON-327/CON-336: the structured table tools, table_sql and table_graphql on the same tables-v1 or flexibility questions, with McNemar and Wilcoxon between each pair; runs `claude -p` on your Claude subscription)
              tables-linked [--config hybrid] [--model claude-sonnet-5-5] [--effort medium] [--ids a,b] [--limit-tasks N] [--workers 1] [--agent-dir <dir>] [--stall-minutes 30] [--reserve] [--min-free-gb 8] [--out <file>]   (CON-312: the linked-set reports asked with all six tools, with passage links (both-linked) and without (both-unlinked), scored on the passage-link bar; --reserve asks the sealed reserve set once instead, after checking its pinned SHA-256; each run waits while free memory is under --min-free-gb; runs `claude -p` on your Claude subscription)
              tables-nested [--config hybrid] [--split test|tune] [--model claude-sonnet-5-5] [--effort medium] [--ids a,b] [--limit-tasks N] [--workers 1] [--agent-dir <dir>] [--stall-minutes 30] [--min-free-gb 8] [--out <file>]   (CON-335: nested JSON, JSON Lines, XML and YAML files asked with the four table tools, scored on the nested-files bar; runs `claude -p` on your Claude subscription)
              tables-arms-score --runs-file <runs.jsonl> [--out <file>] [the tables-arms options the run used]   (scores a finished tables-arms run from its .runs.jsonl without asking again)
              code     [--config code] [--split heldout|tuning] [--out <file>]   (code-v1: code tools against grep)
            """;
}

internal static class ReportWriter
{
    public static void Write(RunFolder run, RunScores scores)
    {
        run.WriteText("report.html", HtmlReport.RenderRun(scores, run));
        run.WriteText("report.json", JsonSerializer.Serialize(scores, EvalJson.Options));
    }
}
