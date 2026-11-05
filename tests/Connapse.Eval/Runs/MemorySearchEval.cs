using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Eval.Cli;
using Connapse.Eval.Datasets;
using Connapse.Eval.Model;
using Connapse.Eval.Runs.MemorySearch;
using Connapse.Eval.Statistics;
using Connapse.Eval.Systems;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Eval.Runs;

/// <summary>
/// The memory-search-v1 suite (CON-244): does container memory make agent search better? The same agent answers
/// each task twice on one Connapse instance, through the MCP server's own tool methods:
/// <list type="bullet">
/// <item><b>Off</b>: <c>search_knowledge</c> and <c>get_document</c>, as an agent searches today.</item>
/// <item><b>On</b>: the same, plus <c>memory_recall</c> over the facts agents stored about the corpus.</item>
/// </list>
/// A run scores 1 when the agent cites a gold document and no stale one. The verdict follows the bar in
/// <c>eval/pass-bars.md</c>, written before any scored run.
/// </summary>
public sealed class MemorySearchEval(RepoPaths paths, HttpClient http, TextWriter log)
{
    public const string Dataset = "memory-search";

    public static readonly string[] OffTools = ["search_knowledge", "get_document"];
    public static readonly string[] OnTools = [.. OffTools, "memory_recall"];

    public sealed record Options(string Config, string Split, string Model, string Effort, int Repetitions, int? LimitTasks, int Workers, string? OutPath);

    public sealed record MemoryOp(string Op, string Id, string? Target, string? Subject, string? Attribute, string Value,
        string? ValidFrom, IReadOnlyList<string> Sources, string? Reason);

    /// <summary>One arm's runs of one task, in repetition order.</summary>
    public sealed record TaskResult(SearchTask Task, IReadOnlyList<(AgentRun Run, RunScore Score)> Off, IReadOnlyList<(AgentRun Run, RunScore Score)> On);

    public async Task<int> RunAsync(Options options, CancellationToken ct)
    {
        // Checked first, so a missing key fails before containers start.
        string apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } key
            ? key
            : throw new InvalidOperationException("Set ANTHROPIC_API_KEY to run the memory-search agents.");

        DatasetEntry entry = EvalManifest.Load(paths.ManifestPath).Datasets[Dataset];
        DatasetCache cache = new(paths.CacheRoot, http, paths.DatasetsRoot);
        await cache.EnsureAsync(Dataset, entry, allowUnpinned: false, ct);
        string root = cache.DirectoryFor(Dataset, entry);
        List<SearchTask> tasks = [.. ReadTasks(Path.Combine(root, "tasks.jsonl")).Where(t => t.Split == options.Split)];
        if (options.LimitTasks is int limit)
            tasks = [.. tasks.Take(limit)];
        List<EvalDocument> corpus = ReadCorpus(Path.Combine(root, "corpus.jsonl"));
        List<MemoryOp> ops = ReadMemory(Path.Combine(root, "memory.jsonl"));

        SystemConfig config = SystemConfig.Load(paths.EvalRoot, "connapse", options.Config);
        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        await using ConnapseSearchSystem system = await ConnapseSearchSystem.StartAsync(config, paths.WebContentRoot, embeddings, log, null, ct);
        await system.IndexAsync(new EvalDataset(Dataset, entry.Version, [], corpus, [], new Qrels()), ct);
        (Guid container, IReadOnlyDictionary<string, string> docMap) = system.ContainerOf(Dataset);

        Guid userId = Guid.NewGuid();
        await ReplayAsync(system.Services, container, docMap, ops, new MemoryCaller(userId, null, "memory-search-eval"), ct);
        log.WriteLine($"{corpus.Count} documents indexed, {ops.Count} memory operations replayed");

        var principal = McpToolbox.Reader(userId, "memory-search-eval");
        McpToolbox off = new(system.Services, principal, OffTools);
        McpToolbox on = new(system.Services, principal, OnTools);
        DocumentIndex index = new(docMap, corpus.ToDictionary(d => d.Id, d => d.FileName!, StringComparer.Ordinal));
        string prompt = SystemPrompt(container);
        ClaudeAgentModel model = new(new AnthropicClient { ApiKey = apiKey, MaxRetries = 3 }, options.Model, options.Effort);

        // Each task's repetitions run Off then On back to back, so drift in the API hits both arms alike.
        List<(SearchTask Task, int Rep, bool Memory)> jobs =
            [.. tasks.SelectMany(t => Enumerable.Range(0, options.Repetitions).SelectMany(r => new[] { (t, r, false), (t, r, true) }))];
        ConcurrentDictionary<(string, int, bool), (AgentRun, RunScore)> done = new();
        int finished = 0;
        await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = options.Workers, CancellationToken = ct }, async (job, token) =>
        {
            AgentRun run = await AgentLoop.RunAsync(model, job.Memory ? on : off, prompt, job.Task.Question, token);
            RunScore score = MemorySearchScoring.Score(job.Task, run, index);
            done[(job.Task.Id, job.Rep, job.Memory)] = (run, score);
            int n = Interlocked.Increment(ref finished);
            log.WriteLine($"[{n}/{jobs.Count}] {job.Task.Id} rep {job.Rep + 1} {(job.Memory ? "on " : "off")}: " +
                $"{run.Outcome} score {score.Score} calls {score.ToolCalls}{(run.Error is null ? "" : $" ({run.Error})")}");
        });

        List<TaskResult> results = [.. tasks.Select(t => new TaskResult(t,
            [.. Enumerable.Range(0, options.Repetitions).Select(r => done[(t.Id, r, false)])],
            [.. Enumerable.Range(0, options.Repetitions).Select(r => done[(t.Id, r, true)])]))];

        JsonObject report = Report(options, results);
        string json = report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (options.OutPath is not null)
        {
            await File.WriteAllTextAsync(options.OutPath, json + "\n", ct);
            await File.WriteAllLinesAsync(Path.ChangeExtension(options.OutPath, ".runs.jsonl"), RunLines(results, index), ct);
        }
        log.WriteLine(json);
        return (bool)report["gate"]!["passed"]! ? 0 : 3;
    }

    /// <summary>The same instructions for both arms; they name the container and never mention memory.</summary>
    public static string SystemPrompt(Guid container) =>
        $"You answer questions about a company's internal documents, stored in the Connapse container with ID {container}. " +
        "Use the tools to find the document that answers the question, and read a document in full with get_document " +
        "when a search result is not enough. When you are sure, call the answer tool with a short answer and the " +
        $"DocumentId of each document your answer relies on. You may make at most {AgentLoop.MaxToolCalls} tool calls before answering.";

    /// <summary>The agents' memory, written as they wrote it: each fact cites its source documents.</summary>
    public static async Task ReplayAsync(IServiceProvider services, Guid container, IReadOnlyDictionary<string, string> docMap,
        IReadOnlyList<MemoryOp> ops, MemoryCaller caller, CancellationToken ct)
    {
        Dictionary<string, string> documentOf = docMap.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IMemoryService memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        Dictionary<string, (Guid Fact, string Subject, string Attribute)> written = new(StringComparer.Ordinal);
        foreach (MemoryOp op in ops)
        {
            (string subject, string attribute) = op.Op == "remember"
                ? (op.Subject!, op.Attribute!)
                : (written[op.Target!].Subject, written[op.Target!].Attribute);
            var fact = new NewMemoryFact(subject, attribute, op.Value, Date(op.ValidFrom), null,
                [.. op.Sources.Select(s => new MemorySource(MemorySourceKind.Document, documentOf[s]))], op.Reason);
            MemoryWriteResult result = op.Op == "remember"
                ? await memory.RememberAsync(container, fact, caller, ct)
                : await memory.CorrectAsync(container, written[op.Target!].Fact, MemoryCorrection.Changed, fact, caller, ct);
            if (result.Fact is null)
                throw new InvalidOperationException($"{op.Id} was refused: {result.Error}");
            written[op.Id] = (result.Fact.Id, subject, attribute);
        }
    }

    public static JsonObject Report(Options options, IReadOnlyList<TaskResult> results)
    {
        List<TaskResult> m = [.. results.Where(r => r.Task.InMemorySet)];
        List<TaskResult> h = [.. results.Where(r => !r.Task.InMemorySet)];
        PairedComparison? mCompare = Compare(m, s => s.Score);
        PairedComparison? hCompare90 = Compare(h, s => s.Score, 0.90);
        PairedComparison? allCompare = Compare(results, s => s.Score);
        Verdict? verdict = mCompare is not null && hCompare90 is not null && allCompare is not null
            ? MemorySearchScoring.Decide(mCompare, hCompare90, allCompare)
            : null;

        JsonObject kinds = [];
        foreach (string kind in results.Select(r => r.Task.Kind).Distinct().Order(StringComparer.Ordinal))
            kinds[kind] = Group([.. results.Where(r => r.Task.Kind == kind)]);
        JsonObject halves = new()
        {
            ["documented"] = Group([.. m.Where(r => r.Task.Documented == true)]),
            ["undocumented"] = Group([.. m.Where(r => r.Task.Documented == false)]),
        };

        return new JsonObject
        {
            ["config"] = options.Config,
            ["split"] = options.Split,
            ["model"] = options.Model,
            ["effort"] = options.Effort,
            ["repetitions"] = options.Repetitions,
            ["tasks"] = results.Count,
            ["all"] = Group(results),
            ["memorySet"] = Group(m),
            ["holdoutSet"] = Group(h),
            ["kinds"] = kinds,
            ["memorySetHalves"] = halves,
            ["comparisons"] = new JsonObject
            {
                ["memorySetScore95"] = Comparison(mCompare),
                ["holdoutSetScore90"] = Comparison(hCompare90),
                ["allScore95"] = Comparison(allCompare),
                ["answerAccuracy"] = Comparison(Compare(results, s => s.AnswerRight ? 1 : 0)),
                ["staleCitations"] = Comparison(Compare(results, s => s.CitedStale ? 1 : 0)),
                ["runRecall"] = Comparison(Compare(results, s => s.SawGold ? 1 : 0)),
                ["firstQueryMrr@10"] = Comparison(Compare(results, s => s.FirstQueryMrr)),
                ["searchCalls"] = Comparison(Compare(results, s => s.Searches)),
                ["toolCalls"] = Comparison(Compare(results, s => s.ToolCalls)),
            },
            ["outcomes"] = new JsonObject
            {
                ["off"] = Outcomes(results.SelectMany(r => r.Off)),
                ["on"] = Outcomes(results.SelectMany(r => r.On)),
            },
            ["tokens"] = new JsonObject
            {
                ["off"] = Tokens(options.Model, results.SelectMany(r => r.Off)),
                ["on"] = Tokens(options.Model, results.SelectMany(r => r.On)),
            },
            ["examples"] = new JsonObject
            {
                ["memoryHelped"] = Examples(results, better: true),
                ["memoryHurt"] = Examples(results, better: false),
            },
            ["gate"] = new JsonObject
            {
                ["verdict"] = verdict is { } v ? MemorySearchScoring.Label(v) : "not scored (fewer than 2 tasks in a set)",
                ["memorySetMdeOver015"] = mCompare is null ? null : mCompare.MinimumDetectableEffect > 0.15,
                ["passed"] = verdict == Verdict.Keep,
            },
        };
    }

    private static double Mean(IEnumerable<(AgentRun Run, RunScore Score)> runs, Func<RunScore, double> measure) =>
        runs.Select(r => measure(r.Score)).DefaultIfEmpty(0).Average();

    private static PairedComparison? Compare(IReadOnlyList<TaskResult> group, Func<RunScore, double> measure, double level = 0.95) =>
        group.Count < 2
            ? null
            : PairedStats.Compare([.. group.Select(r => Mean(r.Off, measure))], [.. group.Select(r => Mean(r.On, measure))], confidenceLevel: level);

    private static JsonObject Group(IReadOnlyList<TaskResult> group) => new()
    {
        ["tasks"] = group.Count,
        ["offScore"] = Round(group.Select(r => Mean(r.Off, s => s.Score)).DefaultIfEmpty(0).Average()),
        ["onScore"] = Round(group.Select(r => Mean(r.On, s => s.Score)).DefaultIfEmpty(0).Average()),
        ["offSearches"] = Round(group.Select(r => Mean(r.Off, s => s.Searches)).DefaultIfEmpty(0).Average()),
        ["onSearches"] = Round(group.Select(r => Mean(r.On, s => s.Searches)).DefaultIfEmpty(0).Average()),
    };

    private static JsonNode? Comparison(PairedComparison? c) => c is null ? null : new JsonObject
    {
        ["tasks"] = c.N,
        ["meanDifference"] = Round(c.MeanDifference),
        ["ci"] = new JsonArray(Round(c.CiLow), Round(c.CiHigh)),
        ["permutationP"] = c.PermutationP,
        ["minimumDetectableEffect"] = Round(c.MinimumDetectableEffect),
    };

    private static JsonObject Outcomes(IEnumerable<(AgentRun Run, RunScore Score)> runs)
    {
        JsonObject counts = [];
        foreach (IGrouping<AgentOutcome, (AgentRun Run, RunScore Score)> g in runs.GroupBy(r => r.Run.Outcome).OrderBy(g => g.Key))
            counts[g.Key.ToString()] = g.Count();
        return counts;
    }

    /// <summary>Token totals, and their cost at list price for the models whose price this knows.</summary>
    private static JsonObject Tokens(string model, IEnumerable<(AgentRun Run, RunScore Score)> runs)
    {
        List<TokenUsage> usage = [.. runs.Select(r => r.Run.Usage)];
        TokenUsage total = usage.Aggregate(TokenUsage.Zero, (a, b) => a.Add(b));
        double? cost = model == "claude-sonnet-5-5"
            ? (total.Input * 2.0 + total.CacheWrite * 2.5 + total.CacheRead * 0.2 + total.Output * 10.0) / 1_000_000
            : null;
        return new JsonObject
        {
            ["runs"] = usage.Count,
            ["input"] = total.Input,
            ["cacheWrite"] = total.CacheWrite,
            ["cacheRead"] = total.CacheRead,
            ["output"] = total.Output,
            ["costUsd"] = cost is null ? null : Math.Round(cost.Value, 4),
        };
    }

    /// <summary>The tasks memory moved most, either way, with what each arm answered and cited first.</summary>
    private static JsonArray Examples(IReadOnlyList<TaskResult> results, bool better)
    {
        IEnumerable<TaskResult> moved = results
            .Select(r => (Result: r, Delta: Mean(r.On, s => s.Score) - Mean(r.Off, s => s.Score)))
            .Where(x => better ? x.Delta > 0 : x.Delta < 0)
            .OrderByDescending(x => Math.Abs(x.Delta))
            .ThenBy(x => x.Result.Task.Id, StringComparer.Ordinal)
            .Take(3)
            .Select(x => x.Result);
        return [.. moved.Select(r => (JsonNode)new JsonObject
        {
            ["task"] = r.Task.Id,
            ["kind"] = r.Task.Kind,
            ["question"] = r.Task.Question,
            ["expected"] = r.Task.Answer,
            ["offScore"] = Round(Mean(r.Off, s => s.Score)),
            ["onScore"] = Round(Mean(r.On, s => s.Score)),
            ["offAnswer"] = r.Off[0].Run.Answer,
            ["onAnswer"] = r.On[0].Run.Answer,
        })];
    }

    private static IEnumerable<string> RunLines(IReadOnlyList<TaskResult> results, DocumentIndex index)
    {
        foreach (TaskResult r in results)
            foreach ((string arm, IReadOnlyList<(AgentRun Run, RunScore Score)> runs) in new[] { ("off", r.Off), ("on", r.On) })
                for (int i = 0; i < runs.Count; i++)
                {
                    (AgentRun run, RunScore score) = runs[i];
                    yield return new JsonObject
                    {
                        ["task"] = r.Task.Id,
                        ["arm"] = arm,
                        ["rep"] = i + 1,
                        ["outcome"] = run.Outcome.ToString(),
                        ["score"] = score.Score,
                        ["answer"] = run.Answer,
                        ["cited"] = new JsonArray([.. run.Cited.Select(c => (JsonNode?)(index.Resolve(c) ?? c))]),
                        ["calls"] = new JsonArray([.. run.Calls.Select(c => (JsonNode?)new JsonObject
                        {
                            ["tool"] = c.Name,
                            ["input"] = JsonSerializer.SerializeToNode(c.Input),
                        })]),
                        ["error"] = run.Error,
                    }.ToJsonString();
                }
    }

    private static double Round(double x) => Math.Round(x, 4);

    private static DateTimeOffset? Date(string? text) => text is null ? null : MemoryEval.Dates(text)[0];

    public static List<SearchTask> ReadTasks(string path) =>
        [.. File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()).Select(t => new SearchTask(
            (string)t["id"]!, (string)t["kind"]!, (string)t["split"]!, (bool?)t["documented"], (string)t["question"]!,
            [.. t["gold"]!.AsArray().Select(g => (string)g!)], [.. t["stale"]!.AsArray().Select(s => (string)s!)],
            (string)t["answer"]!, (string?)t["staleAnswer"]))];

    /// <summary>Each document uploaded under its title as a .txt file in the root folder; its text already starts with the title.</summary>
    public static List<EvalDocument> ReadCorpus(string path) =>
        [.. File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()).Select(d =>
            new EvalDocument((string)d["id"]!, DocumentKind.Text, (string)d["title"]!, (string)d["text"]!, null, new Dictionary<string, string>())
            {
                FileName = ConnapseSearchSystem.SafeFileName((string)d["title"]!) + ".txt",
                Folder = "/",
            })];

    public static List<MemoryOp> ReadMemory(string path) =>
        [.. File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()).Select(o => new MemoryOp(
            (string)o["op"]!, (string)o["id"]!, (string?)o["target"], (string?)o["subject"], (string?)o["attribute"],
            (string)o["value"]!, (string?)o["validFrom"], [.. o["sources"]!.AsArray().Select(s => (string)s!)], (string?)o["reason"]))];
}
