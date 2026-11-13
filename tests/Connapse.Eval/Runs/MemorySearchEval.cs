using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Eval.Cli;
using Connapse.Eval.Datasets;
using Connapse.Eval.Model;
using Connapse.Eval.Runs.MemorySearch;
using Connapse.Eval.Systems;

namespace Connapse.Eval.Runs;

/// <summary>
/// The memory-search-v1 suite: how well an agent answers with search alone, through the MCP server's own tool methods
/// <c>search_knowledge</c> and <c>get_document</c>. CON-244 compared this with container memory, which CON-255 removed,
/// so this memory-off score is the one to match. A run scores 1 when the agent cites a gold document and no stale one.
/// The rules are in <c>eval/pass-bars.md</c>, written before any scored run.
/// </summary>
public sealed class MemorySearchEval(RepoPaths paths, HttpClient http, TextWriter log)
{
    public const string Dataset = "memory-search";

    public static readonly string[] SearchTools = ["search_knowledge", "get_document"];

    /// <summary>The registered protocol fixes search at its default 10 results in Hybrid mode, so the agent can't change either.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Pinned = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["search_knowledge"] = ["mode", "topK"],
    };

    public const string RegisteredConfig = "hybrid";
    public const string RegisteredModel = "claude-sonnet-5-5";
    public const string RegisteredEffort = "medium";

    public sealed record Options(string Config, string Split, string Model, string Effort, int Repetitions, int? LimitTasks, int Workers, string? OutPath,
        string? AgentDir = null, string? ToolList = null, bool? ToolSearch = null);

    /// <summary>The tool lists a CON-346 arm serves the agent from the real MCP server; unset, the agent sees only <see cref="SearchTools"/>.</summary>
    public static readonly string[] ToolLists = ["full", "compact"];

    /// <summary>The <c>--tool-list</c> value checked, or an <see cref="ArgumentException"/>.</summary>
    public static string? ParseToolList(string? value) =>
        value is null || ToolLists.Contains(value, StringComparer.Ordinal)
            ? value
            : throw new ArgumentException($"--tool-list must be {string.Join(" or ", ToolLists)}, not {value}");

    /// <summary>The <c>--tool-search</c> value: on, off, or unset (Claude Code's own default).</summary>
    public static bool? ParseToolSearch(string? value) => value switch
    {
        null => null,
        "on" => true,
        "off" => false,
        _ => throw new ArgumentException($"--tool-search must be on or off, not {value}"),
    };

    /// <summary>One task's runs, in repetition order.</summary>
    public sealed record TaskResult(SearchTask Task, IReadOnlyList<(AgentRun Run, RunScore Score)> Runs);

    public async Task<int> RunAsync(Options options, CancellationToken ct)
    {
        // The agents run in an empty folder outside the repository, so no project CLAUDE.md is near them.
        string agentDir = Path.GetFullPath(options.AgentDir ?? Path.Combine(Path.GetTempPath(), "connapse-memory-search-agent"));
        Directory.CreateDirectory(agentDir);
        RequireNoProjectMemory(agentDir);

        DatasetEntry entry = EvalManifest.Load(paths.ManifestPath).Datasets[Dataset];
        DatasetCache cache = new(paths.CacheRoot, http, paths.DatasetsRoot);
        await cache.EnsureAsync(Dataset, entry, allowUnpinned: false, ct);
        string root = cache.DirectoryFor(Dataset, entry);
        List<SearchTask> tasks = [.. ReadTasks(Path.Combine(root, "tasks.jsonl")).Where(t => t.Split == options.Split)];
        if (options.LimitTasks is int limit)
            tasks = [.. tasks.Take(limit)];
        List<EvalDocument> corpus = ReadCorpus(Path.Combine(root, "corpus.jsonl"));
        using FreeMemoryFloor floor = FreeMemoryFloor.Start(options.ToolList is null ? null : ToolRoutingEval.FreeMemoryFloorMb, log, ct);
        ct = floor.Token;

        SystemConfig config = SystemConfig.Load(paths.EvalRoot, "connapse", options.Config);
        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        await using ConnapseSearchSystem system = await ConnapseSearchSystem.StartAsync(config, paths.WebContentRoot, embeddings, log, null, ct);
        IndexReport indexed = await system.IndexAsync(new EvalDataset(Dataset, entry.Version, [], corpus, [], new Qrels()), ct);
        (Guid container, IReadOnlyDictionary<string, string> docMap) = system.ContainerOf(Dataset);
        RequireCompleteIndex(indexed, corpus.Count, docMap.Count);
        log.WriteLine($"{corpus.Count} documents indexed");

        // A CON-346 arm serves the real server's tool list, signed in as the eval admin; otherwise the two search tools alone.
        using HttpClient? admin = options.ToolList is null ? null : system.CreateAdminClient();
        await using ServerToolbox? server = admin is null ? null
            : await ServerToolbox.ConnectAsync(admin, ServerToolbox.PathOf(options.ToolList == "full"), Pinned, ct);
        IToolbox tools = server ?? (IToolbox)new McpToolbox(system.Services, McpToolbox.Reader(Guid.NewGuid(), "memory-search-eval"), SearchTools, Pinned);
        if (server is not null)
            log.WriteLine($"{options.ToolList} list: {server.Tools.Count} tools");
        DocumentIndex index = new(docMap, corpus.ToDictionary(d => d.Id, d => d.FileName!, StringComparer.Ordinal));
        await using ToolBridge bridge = new();
        await bridge.StartAsync(ct);
        ClaudeCliAgent agent = new(bridge, options.Model, options.Effort, agentDir, toolSearch: options.ToolSearch);
        await AnswerAndReportAsync(agent, tools, SystemPrompt(container), tasks, index, options, RegisteredTasks, log, ct);
        return 0;
    }

    /// <summary>Runs every task's repetitions, then writes the report and, with <c>--out</c>, its run log.</summary>
    public static async Task AnswerAndReportAsync(ClaudeCliAgent agent, IToolbox tools, string prompt, IReadOnlyList<SearchTask> tasks, DocumentIndex index,
        Options options, int registeredTasks, TextWriter log, CancellationToken ct)
    {
        List<(SearchTask Task, int Rep)> jobs = [.. tasks.SelectMany(t => Enumerable.Range(0, options.Repetitions).Select(r => (t, r)))];
        ConcurrentDictionary<(string, int), (AgentRun, RunScore)> done = new();
        int finished = 0;
        // CON-346 arms retry a provider error, as eval/pass-bars.md registers; earlier suites score it 0 at once.
        int attempts = options.ToolList is null ? 1 : ToolRoutingEval.ProviderAttempts;
        await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = options.Workers, CancellationToken = ct }, async (job, token) =>
        {
            AgentRun run = await agent.RunAsync(tools, prompt, job.Task.Question, token);
            for (int attempt = 1; attempt < attempts && run.Outcome == AgentOutcome.Error; attempt++)
                run = await agent.RunAsync(tools, prompt, job.Task.Question, token);
            RunScore score = MemorySearchScoring.Score(job.Task, run, index);
            done[(job.Task.Id, job.Rep)] = (run, score);
            int n = Interlocked.Increment(ref finished);
            log.WriteLine($"[{n}/{jobs.Count}] {job.Task.Id} rep {job.Rep + 1}: " +
                $"{run.Outcome} score {score.Score} calls {score.ToolCalls}{(run.Error is null ? "" : $" ({run.Error})")} | {agent.LastLimit}");
        });

        List<TaskResult> results = [.. tasks.Select(t => new TaskResult(t,
            [.. Enumerable.Range(0, options.Repetitions).Select(r => done[(t.Id, r)])]))];

        JsonObject report = Report(options, results, registeredTasks);
        report["subscription"] = new JsonObject
        {
            ["atFirstRun"] = agent.FirstLimit?.ToString(),
            ["atLastRun"] = agent.LastLimit?.ToString(),
        };
        string json = report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (options.OutPath is not null)
        {
            await File.WriteAllTextAsync(options.OutPath, json + "\n", ct);
            await File.WriteAllLinesAsync(Path.ChangeExtension(options.OutPath, ".runs.jsonl"), RunLines(results, index), ct);
        }
        log.WriteLine(json);
    }

    /// <summary>The agent's instructions; they name the container and never mention memory.</summary>
    public static string SystemPrompt(Guid container) =>
        $"You answer questions about a company's internal documents, stored in the Connapse container with ID {container}. " +
        "Use the tools to find the document that answers the question, and read a document in full with get_document " +
        "when a search result is not enough. When you are sure, call the answer tool with a short answer and the " +
        $"DocumentId of each document your answer relies on. You may make at most {AgentContract.MaxToolCalls} tool calls before answering.";

    /// <summary>The pre-registered run: every test task, three repetitions. Any other run is diagnostic only.</summary>
    public const int RegisteredTasks = 112;
    public const int RegisteredRepetitions = 3;

    /// <summary>Stops before any agent runs when a document failed to index, because search could never return it.</summary>
    public static void RequireCompleteIndex(IndexReport report, int corpusDocuments, int mappedDocuments)
    {
        if (report.Failed > 0 || mappedDocuments != corpusDocuments)
            throw new InvalidOperationException(
                $"{report.Failed} of {corpusDocuments} documents failed to index and {mappedDocuments} have a Connapse id "
                + $"({string.Join(", ", report.FailedDocumentIds.Take(10))}); scores are comparable only on a complete corpus.");
    }

    /// <summary>
    /// Stops before any agent runs when a CLAUDE.md could reach the agents: Claude Code loads one from its working folder
    /// and every folder above it.
    /// </summary>
    public static void RequireNoProjectMemory(string agentDir)
    {
        for (DirectoryInfo? dir = new(agentDir); dir is not null; dir = dir.Parent)
            foreach (string name in (string[])["CLAUDE.md", "CLAUDE.local.md", Path.Combine(".claude", "CLAUDE.md")])
                if (File.Exists(Path.Combine(dir.FullName, name)))
                    throw new InvalidOperationException(
                        $"{Path.Combine(dir.FullName, name)} would reach the agents; run them from a folder with no CLAUDE.md in it or above it.");
    }

    /// <summary>Why a run is diagnostic only: anything short of the pre-registered run. Empty when it conforms.</summary>
    public static List<string> Nonconforming(Options options, IReadOnlyList<TaskResult> results, int registeredTasks = RegisteredTasks)
    {
        List<string> reasons = [];
        if (options.Split != "test")
            reasons.Add($"split is {options.Split}, not test");
        if (options.Config != RegisteredConfig)
            reasons.Add($"config is {options.Config}, not {RegisteredConfig}");
        if (options.Model != RegisteredModel)
            reasons.Add($"model is {options.Model}, not {RegisteredModel}");
        if (options.Effort != RegisteredEffort)
            reasons.Add($"effort is {options.Effort}, not {RegisteredEffort}");
        if (options.LimitTasks is not null)
            reasons.Add("--limit-tasks was set");
        if (results.Count != registeredTasks)
            reasons.Add($"{results.Count} tasks, not {registeredTasks}");
        if (options.Repetitions != RegisteredRepetitions)
            reasons.Add($"{options.Repetitions} repetitions, not {RegisteredRepetitions}");
        int incomplete = results.Count(r => r.Runs.Count != options.Repetitions);
        if (incomplete > 0)
            reasons.Add($"{incomplete} tasks are missing runs");
        if (InvalidToolSearchArm(options.ToolSearch, results.SelectMany(r => r.Runs).Select(r => r.Run)))
            reasons.Add("tool search was on but no run called ToolSearch, so the arm is invalid and is rerun");
        return reasons;
    }

    /// <summary>
    /// A tool-search-on arm whose agents never called ToolSearch over the whole task set measured nothing about tool
    /// search, so <c>eval/pass-bars.md</c> (CON-346) calls it invalid.
    /// </summary>
    public static bool InvalidToolSearchArm(bool? toolSearch, IEnumerable<AgentRun> runs) =>
        toolSearch == true && !runs.Any(r => r.ToolSearchCalls > 0);

    /// <summary>The CON-346 measures over every run: what each tool list cost the agent, and how often its calls failed.</summary>
    public static JsonObject ToolMeasures(IReadOnlyList<AgentRun> runs)
    {
        List<EffectiveCall> calls = [.. runs.SelectMany(r => EffectiveCall.Of(r.Calls))];
        int n = Math.Max(1, runs.Count);
        return new JsonObject
        {
            ["runs"] = runs.Count,
            ["findToolsCallsPerRun"] = Round(runs.Sum(r => r.Calls.Count(c => c.Name == Connapse.Web.Mcp.McpToolSearch.FindName)) / (double)n),
            ["toolSearchCallsPerRun"] = Round(runs.Sum(r => r.ToolSearchCalls) / (double)n),
            ["toolCallsPerRun"] = Round(runs.Sum(r => r.Calls.Count) / (double)n),
            ["failedCallsPerRun"] = Round(calls.Count(c => c.Failed) / (double)n),
            ["medianInputTokens"] = Median(runs.Select(r => (double)InputTokens(r.Usage))),
            ["medianSeconds"] = Round(Median(runs.Select(r => r.Seconds))),
        };
    }

    /// <summary>Every input token the model read: fresh, written to cache and read from cache.</summary>
    public static long InputTokens(TokenUsage usage) => usage.Input + usage.CacheWrite + usage.CacheRead;

    public static double Median(IEnumerable<double> values)
    {
        List<double> sorted = [.. values.Order()];
        if (sorted.Count == 0)
            return 0;
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    public static JsonObject Report(Options options, IReadOnlyList<TaskResult> results, int registeredTasks = RegisteredTasks)
    {
        List<string> nonconforming = Nonconforming(options, results, registeredTasks);
        List<TaskResult> m = [.. results.Where(r => r.Task.InMemorySet)];
        List<TaskResult> h = [.. results.Where(r => !r.Task.InMemorySet)];

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
            ["toolList"] = options.ToolList,
            ["toolSearch"] = options.ToolSearch,
            ["tasks"] = results.Count,
            ["all"] = Group(results),
            ["memorySet"] = Group(m),
            ["holdoutSet"] = Group(h),
            ["kinds"] = kinds,
            ["memorySetHalves"] = halves,
            ["measures"] = new JsonObject
            {
                ["answerAccuracy"] = TaskMean(results, s => s.AnswerRight ? 1 : 0),
                ["staleCitations"] = TaskMean(results, s => s.CitedStale ? 1 : 0),
                ["runRecall"] = TaskMean(results, s => s.SawGold ? 1 : 0),
                ["firstQueryMrr@10"] = TaskMean(results, s => s.FirstQueryMrr),
                ["searchCalls"] = TaskMean(results, s => s.Searches),
                ["toolCalls"] = TaskMean(results, s => s.ToolCalls),
            },
            ["outcomes"] = Outcomes(results.SelectMany(r => r.Runs)),
            ["tokens"] = Tokens(results.SelectMany(r => r.Runs)),
            ["toolUse"] = ToolMeasures([.. results.SelectMany(r => r.Runs).Select(r => r.Run)]),
            ["perTask"] = ToolRoutingEval.PerTask(results.Select(r => (r.Task.Id, Mean(r.Runs, s => s.Score)))),
            ["conforming"] = nonconforming.Count == 0,
            ["diagnosticOnly"] = new JsonArray([.. nonconforming.Select(r => (JsonNode)r)]),
        };
    }

    private static double Mean(IEnumerable<(AgentRun Run, RunScore Score)> runs, Func<RunScore, double> measure) =>
        runs.Select(r => measure(r.Score)).DefaultIfEmpty(0).Average();

    /// <summary>A measure averaged per task, then over the tasks, so every task counts the same.</summary>
    private static double TaskMean(IReadOnlyList<TaskResult> group, Func<RunScore, double> measure) =>
        Round(group.Select(r => Mean(r.Runs, measure)).DefaultIfEmpty(0).Average());

    private static JsonObject Group(IReadOnlyList<TaskResult> group) => new()
    {
        ["tasks"] = group.Count,
        ["score"] = TaskMean(group, s => s.Score),
        ["searches"] = TaskMean(group, s => s.Searches),
    };

    private static JsonObject Outcomes(IEnumerable<(AgentRun Run, RunScore Score)> runs)
    {
        JsonObject counts = [];
        foreach (IGrouping<AgentOutcome, (AgentRun Run, RunScore Score)> g in runs.GroupBy(r => r.Run.Outcome).OrderBy(g => g.Key))
            counts[g.Key.ToString()] = g.Count();
        return counts;
    }

    /// <summary>Token totals, and what they would cost at API list price; the run itself is billed to the subscription.</summary>
    private static JsonObject Tokens(IEnumerable<(AgentRun Run, RunScore Score)> runs)
    {
        List<TokenUsage> usage = [.. runs.Select(r => r.Run.Usage)];
        TokenUsage total = usage.Aggregate(TokenUsage.Zero, (a, b) => a.Add(b));
        return new JsonObject
        {
            ["runs"] = usage.Count,
            ["input"] = total.Input,
            ["cacheWrite"] = total.CacheWrite,
            ["cacheRead"] = total.CacheRead,
            ["output"] = total.Output,
            ["apiListPriceUsd"] = Math.Round(total.CostUsd, 4),
        };
    }

    private static IEnumerable<string> RunLines(IReadOnlyList<TaskResult> results, DocumentIndex index)
    {
        foreach (TaskResult r in results)
            for (int i = 0; i < r.Runs.Count; i++)
            {
                (AgentRun run, RunScore score) = r.Runs[i];
                yield return new JsonObject
                {
                    ["task"] = r.Task.Id,
                    ["rep"] = i + 1,
                    ["outcome"] = run.Outcome.ToString(),
                    ["score"] = score.Score,
                    ["answer"] = run.Answer,
                    ["cited"] = new JsonArray([.. run.Cited.Select(c => (JsonNode?)(index.Resolve(c) ?? c))]),
                    ["calls"] = new JsonArray([.. run.Calls.Select(c => (JsonNode?)new JsonObject
                    {
                        ["tool"] = c.Name,
                        ["input"] = JsonSerializer.SerializeToNode(c.Input),
                        ["output"] = c.Output,
                        ["isError"] = c.IsError,
                    })]),
                    ["toolSearchCalls"] = run.ToolSearchCalls,
                    ["inputTokens"] = InputTokens(run.Usage),
                    ["seconds"] = Round(run.Seconds),
                    ["error"] = run.Error,
                }.ToJsonString();
            }
    }

    private static double Round(double x) => Math.Round(x, 4);

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
}
