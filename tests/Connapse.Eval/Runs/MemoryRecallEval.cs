using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Eval.Cli;
using Connapse.Eval.Datasets;
using Connapse.Eval.Runs.MemorySearch;
using Connapse.Eval.Statistics;
using Connapse.Eval.Systems;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Eval.Runs;

/// <summary>
/// The CON-342 proof: agents answer the held-out memory questions with <c>memory_recall</c> alone, from the writer
/// agents' facts replayed into one personal space. The <c>today</c> arm is not offered <c>order</c>; the
/// <c>newest</c> arm is, and is told to use it with a small limit. The rules are in <c>eval/pass-bars.md</c>,
/// written before any run.
/// </summary>
public sealed partial class MemoryRecallEval(RepoPaths paths, HttpClient http, TextWriter log)
{
    public const string SpaceName = "projects";
    public const string RecallTool = "memory_recall";
    public static readonly string[] Kinds = ["current", "as_of", "changed", "when"];
    public const int RegisteredTasks = 152;
    public const int RegisteredRepetitions = 2;

    public const string NewestSentence =
        "Recall with `order` \"newest\" and a small `limit`, and read further only when those facts do not answer the question.";

    public sealed record Options(string Arm, string Config, string Model, string Effort, int Repetitions, int? LimitTasks, int Workers,
        string? OutPath, string? AgentDir);

    /// <summary>One run's measures: whether the answer is right and how many characters of recall results it read.</summary>
    public sealed record RunMeasure(bool Right, int TextRead, int RecallCalls, int OrderedCalls, IReadOnlyList<int> Limits);

    /// <summary>The parameters the arm fixes: <c>containerId</c> in both, and <c>order</c> in <c>today</c>.</summary>
    public static IReadOnlyDictionary<string, string[]> Pinned(string arm) => new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [RecallTool] = arm switch
        {
            "today" => ["containerId", "order"],
            "newest" => ["containerId"],
            _ => throw new ArgumentException("--arm must be today or newest."),
        },
    };

    public async Task<int> RunAsync(Options options, CancellationToken ct)
    {
        IReadOnlyDictionary<string, string[]> pinned = Pinned(options.Arm);
        string agentDir = Path.GetFullPath(options.AgentDir ?? Path.Combine(Path.GetTempPath(), "connapse-memory-recall-agent"));
        Directory.CreateDirectory(agentDir);
        MemorySearchEval.RequireNoProjectMemory(agentDir);

        DatasetEntry entry = EvalManifest.Load(paths.ManifestPath).Datasets[MemoryEval.Dataset];
        DatasetCache cache = new(paths.CacheRoot, http, paths.DatasetsRoot);
        await cache.EnsureAsync(MemoryEval.Dataset, entry, allowUnpinned: false, ct);
        string root = cache.DirectoryFor(MemoryEval.Dataset, entry);
        List<MemoryEval.Question> questions = [.. MemoryEval.ReadQuestions(Path.Combine(root, "questions.jsonl"))
            .Where(q => q.Split == "heldout" && Kinds.Contains(q.Kind))];
        if (options.LimitTasks is int limit)
            questions = [.. questions.Take(limit)];
        JsonObject timeline = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "timeline.json")))!.AsObject();
        string[] codes = [.. MemoryEval.ReadQuestions(Path.Combine(root, "questions.jsonl")).Where(q => q.Split == "heldout")
            .Select(q => q.Code).Distinct().Order(StringComparer.Ordinal)];

        SystemConfig config = SystemConfig.Load(paths.EvalRoot, "connapse", options.Config);
        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        await using ConnapseSearchSystem system = await ConnapseSearchSystem.StartAsync(config, paths.WebContentRoot, embeddings, log, null, ct);

        Guid reader = Guid.NewGuid();
        MemoryCaller owner = new(reader, null, "memory-recall-eval");
        int replayed = await ReplayAsync(system.Services, owner, codes.Select(c => MemoryEval.ReadOps(Path.Combine(root, $"ops-{c}.jsonl"))), ct);
        log.WriteLine($"{replayed} memory operations replayed into '{SpaceName}' from {string.Join(", ", codes)}");

        McpToolbox tools = new(system.Services, McpToolbox.Reader(reader, "memory-recall-eval"), [RecallTool], pinned);
        await using ToolBridge bridge = new();
        await bridge.StartAsync(ct);
        ClaudeCliAgent agent = new(bridge, options.Model, options.Effort, agentDir);
        string prompt = SystemPrompt(options.Arm);

        List<(MemoryEval.Question Q, int Rep)> jobs = [.. questions.SelectMany(q => Enumerable.Range(0, options.Repetitions).Select(r => (q, r)))];
        ConcurrentDictionary<(string, int), (AgentRun Run, RunMeasure Measure)> done = new();
        int finished = 0;
        await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = options.Workers, CancellationToken = ct }, async (job, token) =>
        {
            AgentRun run = await agent.RunAsync(tools, prompt, job.Q.Text, token);
            RunMeasure measure = Measure(job.Q, run, MemoryEval.AskedValues(job.Q, timeline));
            done[(job.Q.Id, job.Rep)] = (run, measure);
            int n = Interlocked.Increment(ref finished);
            log.WriteLine($"[{n}/{jobs.Count}] {job.Q.Id} rep {job.Rep + 1}: {run.Outcome} right {measure.Right} read {measure.TextRead} " +
                $"calls {measure.RecallCalls}{(run.Error is null ? "" : $" ({run.Error})")} | {agent.LastLimit}");
        });

        List<(MemoryEval.Question Q, List<(AgentRun Run, RunMeasure Measure)> Runs)> results = [.. questions.Select(q =>
            (q, Enumerable.Range(0, options.Repetitions).Select(r => done[(q.Id, r)]).ToList()))];
        JsonObject report = Report(options, results);
        report["subscription"] = new JsonObject { ["atFirstRun"] = agent.FirstLimit?.ToString(), ["atLastRun"] = agent.LastLimit?.ToString() };
        string json = report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (options.OutPath is not null)
        {
            await File.WriteAllTextAsync(options.OutPath, json + "\n", ct);
            await File.WriteAllLinesAsync(Path.ChangeExtension(options.OutPath, ".runs.jsonl"), RunLines(results), ct);
        }
        log.WriteLine(json);
        return 0;
    }

    /// <summary>
    /// Creates the owner's personal <c>projects</c> space and replays every project's operations into it in order,
    /// with no sources; a correction targets the fact its op names. Returns how many operations were replayed.
    /// </summary>
    public static async Task<int> ReplayAsync(IServiceProvider services, MemoryCaller owner, IEnumerable<List<MemoryEval.Op>> projects, CancellationToken ct)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        Container space = await scope.ServiceProvider.GetRequiredService<IMemorySpaceStore>().CreatePersonalAsync(owner, SpaceName, null, ct)
            ?? throw new InvalidOperationException($"The '{SpaceName}' space could not be created.");
        IMemoryService memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        Guid container = Guid.Parse(space.Id);
        Dictionary<string, Guid> factOf = new(StringComparer.Ordinal);
        foreach (MemoryEval.Op op in projects.SelectMany(p => p))
        {
            NewMemoryFact fact = new(op.Subject ?? "", op.Attribute ?? "", op.Value,
                op.ValidFrom is null ? null : MemoryEval.Dates(op.ValidFrom)[0], null, null, op.Reason);
            MemoryWriteResult result = op.Verb == "remember"
                ? await memory.RememberAsync(container, fact, owner, ct)
                : await memory.CorrectAsync(factOf[op.Target!],
                    op.Kind == "wrong" ? MemoryCorrection.Wrong : MemoryCorrection.Changed, fact, owner, true, ct);
            factOf[op.Id] = result.Fact?.Id ?? throw new InvalidOperationException($"{op.Id} was refused: {result.Error}");
        }
        return factOf.Count;
    }

    /// <summary>The same in both arms, except that <c>newest</c> gains <see cref="NewestSentence"/>.</summary>
    public static string SystemPrompt(string arm) =>
        $"You answer questions about software projects from the facts saved in the Connapse memory space named \"{SpaceName}\". " +
        $"Read them with {RecallTool}. Give a setting's value exactly as it is written there, a date as YYYY-MM-DD, and for a " +
        "question about a window of time every setting's new value in that window. When you are sure, call the answer tool " +
        "with your answer and an empty documentIds list. " +
        $"You may make at most {AgentContract.MaxToolCalls} tool calls before answering." +
        (arm == "newest" ? " " + NewestSentence : "");

    /// <summary>A run's right answer and the recall text it read; a run that did not answer reads as wrong.</summary>
    public static RunMeasure Measure(MemoryEval.Question q, AgentRun run, IReadOnlyList<string> askedValues)
    {
        List<ToolTrace> recalls = [.. run.Calls.Where(c => c.Name == RecallTool)];
        bool right = run.Outcome == AgentOutcome.Answered && run.Answer is { } answer && Right(q, answer, askedValues);
        return new RunMeasure(right, recalls.Sum(c => c.Output.Length), recalls.Count,
            recalls.Count(c => c.Input.TryGetValue("order", out JsonElement o) && o.ValueKind == JsonValueKind.String),
            [.. recalls.Select(c => c.Input.TryGetValue("limit", out JsonElement l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out int n) ? n : 10)]);
    }

    /// <summary>
    /// The bar's right answer. <c>current</c> and <c>as_of</c>: names the expected value and no other value the setting
    /// had, where a value that is part of the expected one (Redis 7 within Redis 7.2) does not count as another.
    /// <c>when</c>: the expected date and no other. <c>changed</c>: every change's new value.
    /// </summary>
    public static bool Right(MemoryEval.Question q, string answer, IReadOnlyList<string> askedValues)
    {
        switch (q.Kind)
        {
            case "current" or "as_of":
                string expected = (string)q.Answer["value"]!;
                return MemoryEval.Named(answer, expected) >= 0
                    && askedValues.Where(v => v != expected && MemoryEval.Named(expected, v) < 0).All(v => MemoryEval.Named(answer, v) < 0);
            case "when":
                string[] dates = [.. IsoDate().Matches(answer).Select(m => m.Value).Distinct()];
                return dates is [var only] && only == (string)q.Answer["date"]!;
            case "changed":
                return q.Answer["changes"]!.AsArray().All(c => MemoryEval.Named(answer, (string)c!["to"]!) >= 0);
            default:
                throw new ArgumentException($"{q.Id}: {q.Kind} is not a kind this proof scores.");
        }
    }

    private static JsonObject Report(Options options, IReadOnlyList<(MemoryEval.Question Q, List<(AgentRun Run, RunMeasure Measure)> Runs)> results)
    {
        JsonObject Group(IEnumerable<(MemoryEval.Question Q, List<(AgentRun Run, RunMeasure Measure)> Runs)> rows)
        {
            List<List<(AgentRun Run, RunMeasure Measure)>> g = [.. rows.Select(r => r.Runs)];
            double TaskMean(Func<RunMeasure, double> f) => Math.Round(g.Select(rs => rs.Average(r => f(r.Measure))).DefaultIfEmpty(0).Average(), 4);
            return new JsonObject
            {
                ["tasks"] = g.Count,
                ["right"] = TaskMean(m => m.Right ? 1 : 0),
                ["textRead"] = TaskMean(m => m.TextRead),
                ["recallCalls"] = TaskMean(m => m.RecallCalls),
            };
        }

        List<(AgentRun Run, RunMeasure Measure)> all = [.. results.SelectMany(r => r.Runs)];
        List<int> limits = [.. all.SelectMany(r => r.Measure.Limits)];
        JsonObject kinds = [];
        foreach (string kind in Kinds)
            kinds[kind] = Group(results.Where(r => r.Q.Kind == kind));
        List<string> nonconforming = [];
        if (options.Config != MemorySearchEval.RegisteredConfig) nonconforming.Add($"config is {options.Config}");
        if (options.Model != MemorySearchEval.RegisteredModel) nonconforming.Add($"model is {options.Model}");
        if (options.Effort != MemorySearchEval.RegisteredEffort) nonconforming.Add($"effort is {options.Effort}");
        if (results.Count != RegisteredTasks) nonconforming.Add($"{results.Count} tasks, not {RegisteredTasks}");
        if (options.Repetitions != RegisteredRepetitions) nonconforming.Add($"{options.Repetitions} repetitions, not {RegisteredRepetitions}");
        if (options.Workers > 2) nonconforming.Add($"{options.Workers} workers, more than 2");
        TokenUsage tokens = all.Select(r => r.Run.Usage).Aggregate(TokenUsage.Zero, (a, b) => a.Add(b));

        return new JsonObject
        {
            ["arm"] = options.Arm,
            ["config"] = options.Config,
            ["model"] = options.Model,
            ["effort"] = options.Effort,
            ["repetitions"] = options.Repetitions,
            ["all"] = Group(results),
            ["kinds"] = kinds,
            ["recallCallsPassingOrder"] = all.Sum(r => r.Measure.OrderedCalls),
            ["recallCalls"] = all.Sum(r => r.Measure.RecallCalls),
            ["runsPassingOrder"] = all.Count(r => r.Measure.OrderedCalls > 0),
            ["runs"] = all.Count,
            ["limitsPassed"] = new JsonObject([.. limits.GroupBy(l => l).OrderBy(g => g.Key)
                .Select(g => KeyValuePair.Create(g.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), (JsonNode?)g.Count()))]),
            ["outcomes"] = new JsonObject([.. all.GroupBy(r => r.Run.Outcome).OrderBy(g => g.Key)
                .Select(g => KeyValuePair.Create(g.Key.ToString(), (JsonNode?)g.Count()))]),
            ["tokens"] = new JsonObject
            {
                ["input"] = tokens.Input,
                ["cacheWrite"] = tokens.CacheWrite,
                ["cacheRead"] = tokens.CacheRead,
                ["output"] = tokens.Output,
                ["apiListPriceUsd"] = Math.Round(tokens.CostUsd, 4),
            },
            ["conforming"] = nonconforming.Count == 0,
            ["diagnosticOnly"] = new JsonArray([.. nonconforming.Select(r => (JsonNode)r)]),
        };
    }

    /// <summary>One line per run: its measures, answer and the recall calls it made (inputs and result sizes).</summary>
    private static IEnumerable<string> RunLines(IReadOnlyList<(MemoryEval.Question Q, List<(AgentRun Run, RunMeasure Measure)> Runs)> results)
    {
        foreach ((MemoryEval.Question q, List<(AgentRun Run, RunMeasure Measure)> runs) in results)
            for (int i = 0; i < runs.Count; i++)
            {
                (AgentRun run, RunMeasure m) = runs[i];
                yield return new JsonObject
                {
                    ["task"] = q.Id,
                    ["kind"] = q.Kind,
                    ["rep"] = i + 1,
                    ["outcome"] = run.Outcome.ToString(),
                    ["right"] = m.Right ? 1 : 0,
                    ["textRead"] = m.TextRead,
                    ["recallCalls"] = m.RecallCalls,
                    ["orderedCalls"] = m.OrderedCalls,
                    ["answer"] = run.Answer,
                    ["calls"] = new JsonArray([.. run.Calls.Select(c => (JsonNode?)new JsonObject
                    {
                        ["tool"] = c.Name,
                        ["input"] = JsonSerializer.SerializeToNode(c.Input),
                        ["outputChars"] = c.Output.Length,
                    })]),
                    ["tokens"] = run.Usage.Input + run.Usage.Output + run.Usage.CacheRead + run.Usage.CacheWrite,
                    ["error"] = run.Error,
                }.ToJsonString();
            }
    }

    /// <summary>Each task's per-repetition values of one measure (<c>right</c> or <c>textRead</c>) from a run log.</summary>
    public static SortedDictionary<string, List<double>> TaskValues(IEnumerable<string> runLines, string measure)
    {
        SortedDictionary<string, List<double>> values = new(StringComparer.Ordinal);
        foreach (string line in runLines.Where(l => l.Length > 0))
        {
            JsonObject run = JsonNode.Parse(line)!.AsObject();
            string task = (string?)run["task"] ?? throw new ArgumentException("A run line has no task.");
            double value = (double?)run[measure] ?? throw new ArgumentException($"A run of {task} has no {measure}.");
            if (!values.TryGetValue(task, out List<double>? list))
                values[task] = list = [];
            list.Add(value);
        }
        return values;
    }

    /// <summary>
    /// The bar's two rules over paired task means: R1 passes when the 95% BCa upper bound of (newest − today) text read
    /// is below 0; R2 when the 90% BCa lower bound of (newest − today) right answers is above −<paramref name="margin"/>.
    /// </summary>
    public static JsonObject Compare(IReadOnlyList<string> todayLines, IReadOnlyList<string> newestLines, double margin = MemorySearchComparison.DefaultMargin)
    {
        (double[] tRead, double[] nRead) = Paired(TaskValues(todayLines, "textRead"), TaskValues(newestLines, "textRead"));
        (double[] tRight, double[] nRight) = Paired(TaskValues(todayLines, "right"), TaskValues(newestLines, "right"));
        PairedComparison read = PairedStats.Compare(tRead, nRead);
        PairedComparison right = PairedStats.Compare(tRight, nRight, confidenceLevel: 0.90);
        bool r1 = read.CiHigh < 0, r2 = right.CiLow > -margin;
        return new JsonObject
        {
            ["tasks"] = tRead.Length,
            ["r1TextRead"] = new JsonObject
            {
                ["today"] = Math.Round(tRead.Average(), 1),
                ["newest"] = Math.Round(nRead.Average(), 1),
                ["meanDifference"] = Math.Round(read.MeanDifference, 1),
                ["ci95"] = new JsonArray(Math.Round(read.CiLow, 1), Math.Round(read.CiHigh, 1)),
                ["verdict"] = r1 ? "PASS" : "FAIL",
                ["rule"] = "PASS when the 95% BCa upper bound of (newest - today) text read is below 0",
            },
            ["r2Right"] = new JsonObject
            {
                ["today"] = Math.Round(tRight.Average(), 4),
                ["newest"] = Math.Round(nRight.Average(), 4),
                ["meanDifference"] = Math.Round(right.MeanDifference, 4),
                ["ci90"] = new JsonArray(Math.Round(right.CiLow, 4), Math.Round(right.CiHigh, 4)),
                ["verdict"] = r2 ? "PASS" : "FAIL",
                ["rule"] = $"PASS when the 90% BCa lower bound of (newest - today) right answers is above -{margin}",
            },
            ["verdict"] = r1 && r2 ? "PASS" : "FAIL",
        };
    }

    private static (double[] Today, double[] Newest) Paired(SortedDictionary<string, List<double>> today, SortedDictionary<string, List<double>> newest)
    {
        if (!today.Keys.SequenceEqual(newest.Keys, StringComparer.Ordinal))
            throw new ArgumentException("The two arms must cover the same tasks.");
        if (today.Keys.FirstOrDefault(t => today[t].Count != newest[t].Count) is string uneven)
            throw new ArgumentException($"The two arms ran {uneven} a different number of times.");
        return ([.. today.Values.Select(v => v.Average())], [.. newest.Values.Select(v => v.Average())]);
    }

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}")]
    private static partial Regex IsoDate();
}
