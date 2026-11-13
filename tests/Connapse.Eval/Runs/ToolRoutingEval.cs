using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Eval.Cli;
using Connapse.Eval.Datasets;
using Connapse.Eval.Model;
using Connapse.Eval.Runs.MemorySearch;
using Connapse.Eval.Systems;

namespace Connapse.Eval.Runs;

/// <summary>One tool-routing task (<c>eval/datasets/tool-routing/tasks.jsonl</c>), with <c>{u}</c> still in it.</summary>
public sealed record RoutingTask(
    string Id, string Split, string Group, string Gold, string Prompt,
    IReadOnlyList<(string Tool, JsonObject Arguments)> Setup, IReadOnlyDictionary<string, string> Expect)
{
    public const string Placeholder = "{u}";

    /// <summary>The task with every <c>{u}</c> replaced by <paramref name="u"/>, so its objects are its own.</summary>
    public RoutingTask For(string u) => this with
    {
        Prompt = Prompt.Replace(Placeholder, u, StringComparison.Ordinal),
        Setup = [.. Setup.Select(s => (s.Tool, JsonNode.Parse(s.Arguments.ToJsonString().Replace(Placeholder, u, StringComparison.Ordinal))!.AsObject()))],
        Expect = Expect.ToDictionary(e => e.Key, e => e.Value.Replace(Placeholder, u, StringComparison.Ordinal), StringComparer.Ordinal),
    };
}

/// <summary>Whether a routing run reached its gold tool (CON-346, <c>eval/pass-bars.md</c>).</summary>
public static class RoutingScoring
{
    /// <summary>
    /// The gold tool ran within the call cap, directly or through a call tool, without an error, and with the
    /// arguments the task expects: each expected key (alternatives split by <c>|</c>) holds the expected text,
    /// ignoring case. A call past the cap never reaches a tool, so it is not in the trace.
    /// </summary>
    public static bool Succeeded(RoutingTask task, IReadOnlyList<ToolTrace> calls) =>
        EffectiveCall.Of(calls.Take(AgentContract.MaxToolCalls)).Any(c => c.Tool == task.Gold && !c.Failed && Matches(task.Expect, c.Arguments));

    public static bool Matches(IReadOnlyDictionary<string, string> expect, IReadOnlyDictionary<string, JsonElement> args) =>
        expect.All(e => e.Key.Split('|').Any(key => args.TryGetValue(key, out JsonElement v)
            && Text(v).Contains(e.Value, StringComparison.OrdinalIgnoreCase)));

    private static string Text(JsonElement v) => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();
}

/// <summary>
/// The tool-routing suite (CON-346): tasks an agent can finish only by running one named Connapse tool, served from
/// the real MCP server's full or compact list, signed in as the eval admin. It measures whether hiding tools behind
/// <c>find_tools</c> and the call tools costs the agent its way to the right tool. The rules are in
/// <c>eval/pass-bars.md</c>, written before any scored run.
/// </summary>
public sealed class ToolRoutingEval(RepoPaths paths, HttpClient http, TextWriter log)
{
    public const string Dataset = "tool-routing";
    public const int RegisteredTasks = 60;

    /// <summary>Attempts per run when Claude Code itself fails (a provider error); after the last, the run scores 0.</summary>
    public const int ProviderAttempts = 1 + 3;

    /// <summary>A routing run ends with a one-line report of what the agent did; there is nothing to cite.</summary>
    public static readonly AgentRules Rules = new(AgentContract.MaxToolCalls, AgentContract.TypedAnswer, TypedAnswer: true);

    public sealed record Options(string ToolList, bool? ToolSearch, string Split, string Model, string Effort, int Repetitions,
        int? LimitTasks, int Workers, string? OutPath, string? AgentDir = null);

    public sealed record Fixtures(string DocsContainer, string CodeContainer, string MemorySpace,
        IReadOnlyList<(string Path, string Text)> Documents, IReadOnlyList<(string Path, string Text)> Code, IReadOnlyList<JsonObject> Facts);

    public sealed record TaskResult(RoutingTask Task, IReadOnlyList<(AgentRun Run, bool Success)> Runs)
    {
        public double Success => Runs.Count == 0 ? 0 : Runs.Average(r => r.Success ? 1.0 : 0.0);
    }

    public async Task<int> RunAsync(Options options, CancellationToken ct)
    {
        string agentDir = Path.GetFullPath(options.AgentDir ?? Path.Combine(Path.GetTempPath(), "connapse-tool-routing-agent"));
        Directory.CreateDirectory(agentDir);
        MemorySearchEval.RequireNoProjectMemory(agentDir);

        DatasetEntry entry = EvalManifest.Load(paths.ManifestPath).Datasets[Dataset];
        DatasetCache cache = new(paths.CacheRoot, http, paths.DatasetsRoot);
        await cache.EnsureAsync(Dataset, entry, allowUnpinned: false, ct);
        string root = cache.DirectoryFor(Dataset, entry);
        List<RoutingTask> tasks = [.. ReadTasks(Path.Combine(root, "tasks.jsonl")).Where(t => t.Split == options.Split)];
        if (options.LimitTasks is int limit)
            tasks = [.. tasks.Take(limit)];
        Fixtures fixtures = ReadFixtures(Path.Combine(root, "fixtures.json"));

        // Search quality is not what this suite measures, so the documents are embedded by hashing, without Ollama.
        SystemConfig config = SystemConfig.Load(paths.EvalRoot, "connapse", MemorySearchEval.RegisteredConfig);
        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        await using ConnapseSearchSystem system = await ConnapseSearchSystem.StartAsync(
            config, paths.WebContentRoot, embeddings, log, new HashingEmbeddingProvider(), ct);
        await IndexAsync(system, Prefixless(fixtures.DocsContainer), fixtures.Documents, null, entry.Version, ct);
        await IndexAsync(system, Prefixless(fixtures.CodeContainer), fixtures.Code, "Code", entry.Version, ct);

        using HttpClient admin = system.CreateAdminClient();
        await using ServerToolbox setup = await ServerToolbox.ConnectAsync(admin, ServerToolbox.FullPath, null, ct);
        await SeedMemoryAsync(setup, fixtures, ct);
        await using ServerToolbox tools = await ServerToolbox.ConnectAsync(admin, ServerToolbox.PathOf(options.ToolList == "full"), null, ct);
        log.WriteLine($"{options.ToolList} list: {tools.Tools.Count} tools; tool search {Describe(options.ToolSearch)}");

        await using ToolBridge bridge = new();
        await bridge.StartAsync(ct);
        ClaudeCliAgent agent = new(bridge, options.Model, options.Effort, agentDir, rules: Rules, toolSearch: options.ToolSearch);
        string prompt = SystemPrompt(fixtures);

        List<(RoutingTask Task, int Rep)> jobs = [.. tasks.SelectMany(t => Enumerable.Range(0, options.Repetitions).Select(r => (t, r)))];
        ConcurrentDictionary<(string, int), (AgentRun, bool)> done = new();
        int finished = 0;
        await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = options.Workers, CancellationToken = ct }, async (job, token) =>
        {
            (AgentRun run, bool success) = await RunOnceAsync(agent, setup, tools, prompt, job.Task, token);
            done[(job.Task.Id, job.Rep)] = (run, success);
            int n = Interlocked.Increment(ref finished);
            log.WriteLine($"[{n}/{jobs.Count}] {job.Task.Id} rep {job.Rep + 1}: {run.Outcome} {(success ? "reached" : "missed")} {job.Task.Gold} " +
                $"in {run.Calls.Count} calls{(run.Error is null ? "" : $" ({run.Error})")} | {agent.LastLimit}");
        });

        List<TaskResult> results = [.. tasks.Select(t => new TaskResult(t, [.. Enumerable.Range(0, options.Repetitions).Select(r => done[(t.Id, r)])]))];
        JsonObject report = Report(options, results, tools.Tools.Count);
        report["subscription"] = new JsonObject
        {
            ["atFirstRun"] = agent.FirstLimit?.ToString(),
            ["atLastRun"] = agent.LastLimit?.ToString(),
        };
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
    /// One run of a task on its own objects: a fresh <c>{u}</c>, its setup on the full list, then the agent. A run that
    /// ends in a provider error is run again on fresh objects, up to <see cref="ProviderAttempts"/> times.
    /// </summary>
    private static async Task<(AgentRun, bool)> RunOnceAsync(ClaudeCliAgent agent, ServerToolbox setup, ServerToolbox tools, string prompt,
        RoutingTask task, CancellationToken ct)
    {
        AgentRun run = null!;
        for (int attempt = 0; attempt < ProviderAttempts; attempt++)
        {
            RoutingTask instance = task.For(Guid.NewGuid().ToString("N")[..8]);
            foreach ((string tool, JsonObject args) in instance.Setup)
                await CallOrThrowAsync(setup, tool, args, ct);
            run = await agent.RunAsync(tools, prompt, instance.Prompt, ct);
            if (run.Outcome != AgentOutcome.Error)
                return (run, RoutingScoring.Succeeded(instance, run.Calls));
        }
        return (run, false);
    }

    /// <summary>The agent's instructions: where things live and how to finish. They never name a tool.</summary>
    public static string SystemPrompt(Fixtures fixtures) =>
        "You work in a company's Connapse knowledge base. Documents are in the container " +
        $"{fixtures.DocsContainer}, the code is in the container {fixtures.CodeContainer}, and shared facts are in " +
        $"the memory space {fixtures.MemorySpace}. Do the task with the Connapse tools; you are allowed to make the " +
        "change it asks for. When it is done, or you cannot do it, call the answer tool once with a one-sentence " +
        $"report of what you did. You may make at most {AgentContract.MaxToolCalls} tool calls before answering.";

    private static string Describe(bool? toolSearch) => toolSearch switch { true => "on", false => "off", null => "default" };

    /// <summary>The dataset name that indexes into the container named <paramref name="container"/> (eval- is added).</summary>
    private static string Prefixless(string container) =>
        container.StartsWith("eval-", StringComparison.Ordinal) ? container["eval-".Length..] : throw new InvalidDataException($"{container} must start with eval-");

    private static async Task IndexAsync(ConnapseSearchSystem system, string name, IReadOnlyList<(string Path, string Text)> files,
        string? strategy, string version, CancellationToken ct)
    {
        List<EvalDocument> corpus = [.. files.Select(f => new EvalDocument(f.Path, DocumentKind.Text, null, f.Text, null, new Dictionary<string, string>())
        {
            FileName = Path.GetFileName(f.Path),
            Folder = "/" + (Path.GetDirectoryName(f.Path.TrimStart('/'))?.Replace('\\', '/') ?? ""),
            Strategy = strategy,
        })];
        IndexReport report = await system.IndexAsync(new EvalDataset(name, version, [], corpus, [], new Qrels()), ct);
        MemorySearchEval.RequireCompleteIndex(report, corpus.Count, system.ContainerOf(name).DocMap.Count);
    }

    private static async Task SeedMemoryAsync(ServerToolbox setup, Fixtures fixtures, CancellationToken ct)
    {
        await CallOrThrowAsync(setup, "memory_space_create", new JsonObject { ["name"] = fixtures.MemorySpace, ["kind"] = "team" }, ct);
        foreach (JsonObject fact in fixtures.Facts)
        {
            JsonObject args = new() { ["space"] = fixtures.MemorySpace };
            foreach ((string key, JsonNode? value) in fact)
                if (value is not null)
                    args[key] = value.DeepClone();
            await CallOrThrowAsync(setup, "memory_remember", args, ct);
        }
    }

    /// <summary>A setup call that fails is the harness's fault, not the agent's, so it stops the run.</summary>
    private static async Task CallOrThrowAsync(ServerToolbox toolbox, string tool, JsonObject args, CancellationToken ct)
    {
        Dictionary<string, JsonElement> input = args.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value), StringComparer.Ordinal);
        ToolOutput output = await toolbox.InvokeAsync(tool, input, ct);
        if (output.IsError || output.Text.TrimStart().StartsWith("Error", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Setup call {tool} failed: {output.Text}");
    }

    /// <summary>Why a run is diagnostic only: anything short of the pre-registered run. Empty when it conforms.</summary>
    public static List<string> Nonconforming(Options options, IReadOnlyList<TaskResult> results)
    {
        List<string> reasons = [];
        if (options.Split != "test")
            reasons.Add($"split is {options.Split}, not test");
        if (options.Model != MemorySearchEval.RegisteredModel)
            reasons.Add($"model is {options.Model}, not {MemorySearchEval.RegisteredModel}");
        if (options.Effort != MemorySearchEval.RegisteredEffort)
            reasons.Add($"effort is {options.Effort}, not {MemorySearchEval.RegisteredEffort}");
        if (options.ToolSearch is null)
            reasons.Add("--tool-search was not set to on or off");
        if (options.LimitTasks is not null)
            reasons.Add("--limit-tasks was set");
        if (results.Count != RegisteredTasks)
            reasons.Add($"{results.Count} tasks, not {RegisteredTasks}");
        if (options.Repetitions != MemorySearchEval.RegisteredRepetitions)
            reasons.Add($"{options.Repetitions} repetitions, not {MemorySearchEval.RegisteredRepetitions}");
        if (MemorySearchEval.InvalidToolSearchArm(options.ToolSearch, results.SelectMany(r => r.Runs).Select(r => r.Run)))
            reasons.Add("tool search was on but no run called ToolSearch, so the arm is invalid and is rerun");
        return reasons;
    }

    public static JsonObject Report(Options options, IReadOnlyList<TaskResult> results, int listedTools)
    {
        List<string> nonconforming = Nonconforming(options, results);
        JsonObject groups = [];
        foreach (string group in results.Select(r => r.Task.Group).Distinct().Order(StringComparer.Ordinal))
            groups[group] = Math.Round(results.Where(r => r.Task.Group == group).Average(r => r.Success), 4);
        List<AgentRun> runs = [.. results.SelectMany(r => r.Runs).Select(r => r.Run)];
        return new JsonObject
        {
            ["suite"] = Dataset,
            ["toolList"] = options.ToolList,
            ["toolSearch"] = options.ToolSearch,
            ["listedTools"] = listedTools,
            ["split"] = options.Split,
            ["model"] = options.Model,
            ["effort"] = options.Effort,
            ["repetitions"] = options.Repetitions,
            ["tasks"] = results.Count,
            ["success"] = Math.Round(results.Select(r => r.Success).DefaultIfEmpty(0).Average(), 4),
            ["groups"] = groups,
            ["perTask"] = PerTask(results.Select(r => (r.Task.Id, r.Success))),
            ["outcomes"] = new JsonObject([.. runs.GroupBy(r => r.Outcome).OrderBy(g => g.Key)
                .Select(g => new KeyValuePair<string, JsonNode?>(g.Key.ToString(), g.Count()))]),
            ["toolUse"] = MemorySearchEval.ToolMeasures(runs),
            ["conforming"] = nonconforming.Count == 0,
            ["diagnosticOnly"] = new JsonArray([.. nonconforming.Select(r => (JsonNode)r)]),
        };
    }

    /// <summary>Each task's mean score, by task id: what <c>tool-arms</c> pairs across two arms.</summary>
    public static JsonObject PerTask(IEnumerable<(string Id, double Score)> tasks) =>
        new([.. tasks.Select(t => new KeyValuePair<string, JsonNode?>(t.Id, Math.Round(t.Score, 6)))]);

    private static IEnumerable<string> RunLines(IReadOnlyList<TaskResult> results)
    {
        foreach (TaskResult r in results)
            for (int i = 0; i < r.Runs.Count; i++)
            {
                (AgentRun run, bool success) = r.Runs[i];
                yield return new JsonObject
                {
                    ["task"] = r.Task.Id,
                    ["gold"] = r.Task.Gold,
                    ["rep"] = i + 1,
                    ["outcome"] = run.Outcome.ToString(),
                    ["success"] = success,
                    ["answer"] = run.Answer,
                    ["calls"] = new JsonArray([.. run.Calls.Select(c => (JsonNode?)new JsonObject
                    {
                        ["tool"] = c.Name,
                        ["input"] = JsonSerializer.SerializeToNode(c.Input),
                        ["output"] = c.Output.Length > 2000 ? c.Output[..2000] + "…" : c.Output,
                        ["isError"] = c.IsError,
                    })]),
                    ["toolSearchCalls"] = run.ToolSearchCalls,
                    ["inputTokens"] = MemorySearchEval.InputTokens(run.Usage),
                    ["seconds"] = Math.Round(run.Seconds, 2),
                    ["error"] = run.Error,
                }.ToJsonString();
            }
    }

    public static List<RoutingTask> ReadTasks(string path) =>
        [.. File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()).Select(t => new RoutingTask(
            (string)t["id"]!, (string)t["split"]!, (string)t["group"]!, (string)t["gold"]!, (string)t["prompt"]!,
            [.. t["setup"]!.AsArray().Select(s => ((string)s!["tool"]!, s["arguments"]!.AsObject().DeepClone().AsObject()))],
            t["expect"]!.AsObject().ToDictionary(e => e.Key, e => (string)e.Value!, StringComparer.Ordinal)))];

    public static Fixtures ReadFixtures(string path)
    {
        JsonObject f = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        static List<(string, string)> Files(JsonNode? node) => [.. node!.AsArray().Select(d => ((string)d!["path"]!, (string)d["text"]!))];
        return new Fixtures((string)f["docsContainer"]!, (string)f["codeContainer"]!, (string)f["memorySpace"]!,
            Files(f["documents"]), Files(f["code"]), [.. f["facts"]!.AsArray().Select(x => x!.AsObject().DeepClone().AsObject())]);
    }
}
