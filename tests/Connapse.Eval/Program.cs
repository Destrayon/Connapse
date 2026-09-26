using System.Text.Json;
using Connapse.Eval.Cli;
using Connapse.Eval.Datasets;
using Connapse.Eval.Model;
using Connapse.Eval.Runs;
using Connapse.Eval.Systems;

namespace Connapse.Eval;

internal static class EvalEntryPoint
{
    public static async Task<int> Main(string[] args)
    {
        CliArgs cli = CliArgs.Parse(args);
        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            RepoPaths paths = RepoPaths.Find(Directory.GetCurrentDirectory());
            using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(30) };
            return cli.Command switch
            {
                "run" => await Commands.RunAsync(cli, paths, http, cts.Token),
                "pool" => Commands.PoolUnjudged(cli),
                "datasets" => await Commands.DatasetsAsync(cli, paths, http, cts.Token),
                "compare" or "report" => Commands.NotAvailable(cli.Command),
                _ => Commands.Usage(),
            };
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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
        RunRequest request = new(cli.Required("suite"), cli.Option("system") ?? "connapse", cli.Required("config"),
            cli.List("datasets"), cli.Option("resume"), cli.Int("limit-queries"));
        if (request.System != "connapse")
            throw new ArgumentException($"Unknown system '{request.System}'. Known: connapse.");

        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        EvalRunner runner = new(paths, Console.Out, http, async (config, token) =>
            await ConnapseSearchSystem.StartAsync(config, paths.WebContentRoot, embeddings, Console.Out, null, token));
        RunFolder run = await runner.RunAsync(request, ct);

        RunScores scores = Scoring.Score(run);
        ReportWriter.Write(run, scores);
        Console.WriteLine(run.Path);
        foreach (DatasetScores d in scores.Datasets)
            Console.WriteLine(d.Invalid
                ? $"  {d.Name,-28} INVALID"
                : $"  {d.Name,-28} nDCG@10 {d.Means["nDCG@10"]:F3}  MRR@10 {d.Means["MRR@10"]:F3}  judged@10 {d.Means["judged@10"]:F2}");
        Console.WriteLine($"  {"portfolio",-28} nDCG@10 {scores.Portfolio["nDCG@10"]:F3}");
        return scores.Datasets.Any(d => d.Invalid) ? 1 : 0;
    }

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
        DatasetCache cache = new(paths.CacheRoot, http);
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

    public static int NotAvailable(string command)
    {
        Console.Error.WriteLine($"'{command}' is not available yet.");
        return 2;
    }

    public static int Usage()
    {
        Console.Error.WriteLine("""
            usage: dotnet run --project tests/Connapse.Eval -- <command>
              run      --suite <name> --config <name> [--system connapse] [--datasets a,b] [--resume <runDir>] [--limit-queries N]
              compare  <runDirA> <runDirB> [--allow-dataset-mismatch]
              report   <runDir>
              pool     <runDir>... --out <file>
              datasets list | verify | fetch | pin --suite <name> [--datasets a,b]
            """);
        return 2;
    }
}

internal static class ReportWriter
{
    // Replaced in Task 10 with the HTML and JSON report writer.
    public static void Write(RunFolder run, RunScores scores) =>
        run.WriteText("report.json", JsonSerializer.Serialize(scores, EvalJson.Options));
}
