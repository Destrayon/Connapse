using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Eval.Cli;
using Connapse.Eval.Datasets;
using Connapse.Eval.Runs.MemorySearch;
using Connapse.Eval.Systems;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Eval.Runs;

/// <summary>
/// The memory-agent-v1 suite (CON-313): how well a real agent finds and saves facts in its own memory space, through
/// the MCP server's memory tools. Made-up project-manager facts are seeded into a personal space named
/// <see cref="SpaceName"/>; the find arm asks differently worded questions about them, the save arm tells the agent
/// something that is new, changed or already held, and the retrieval arm calls <c>memory_recall</c> with the question
/// alone. The rules and the pass bar are in <c>eval/pass-bars.md</c>, written before any scored run.
/// </summary>
public sealed class MemoryAgentEval(RepoPaths paths, HttpClient http, TextWriter log)
{
    public const string Dataset = "memory-agent";
    public const string SpaceName = "pm-desk";

    /// <summary>The date the save prompt gives as today; every seeded change is earlier.</summary>
    public const string Today = "2026-11-09";

    public static readonly string[] ReadTools = ["memory_spaces", "memory_recall", "memory_timeline"];
    public static readonly string[] WriteTools = [.. ReadTools, "memory_remember", "memory_correct"];
    public static readonly string[] Arms = ["all", "retrieval", "find", "save"];

    /// <summary>Find tasks whose question shares no content word with the fact, or names only part of one.</summary>
    public static readonly string[] HardKinds = ["reworded", "indirect", "partial"];

    public const string RegisteredConfig = "keyword";
    public const string RegisteredModel = "claude-sonnet-5-5";
    public const string RegisteredEffort = "medium";
    public const int RegisteredRepetitions = 3;
    public const int RegisteredFindTasks = 74;
    public const int RegisteredSaveTasks = 24;

    public sealed record Options(string Config, string Split, string Model, string Effort, int Repetitions, int? LimitTasks, int Workers,
        string? OutPath, string? AgentDir = null, string Arm = "all");

    public sealed record SeedHistory(string Value, string ValidFrom, string ChangedOn);

    public sealed record SeedFact(string Key, string Subject, string Attribute, string Value, SeedHistory? History);

    /// <summary>A find task (gold fact keys, empty when memory holds no answer) or a save task (the fact it touches, and the words the saved fact must hold).</summary>
    public sealed record AgentTask(string Id, string Type, string Kind, string Split, string Question, IReadOnlyList<string> Gold,
        string? Target, IReadOnlyList<IReadOnlyList<string>> Expect);

    /// <summary>A seeded space: each key's current fact id, its older versions' ids, and its correction chain.</summary>
    public sealed record Seeded(Guid Space, IReadOnlyDictionary<string, Guid> Current, IReadOnlyDictionary<string, Guid> Old,
        IReadOnlyDictionary<string, Guid> Chain);

    /// <summary>One run's score and the measures reported beside it.</summary>
    public sealed record TaskScore(double Score, IReadOnlyDictionary<string, double> Measures, string? Note = null);

    public sealed record TaskResult(AgentTask Task, IReadOnlyList<(AgentRun Run, TaskScore Score)> Runs);

    public static readonly ToolSpec Answer = new(
        AgentContract.AnswerTool,
        "Give your final answer and the memory facts it rests on. Call this once, when you are done; it ends the task.",
        new Dictionary<string, JsonElement>
        {
            ["answer"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The short answer, or what you did" }),
            ["factIds"] = JsonSerializer.SerializeToElement(new
            {
                type = "array",
                items = new { type = "string" },
                description = "The id of every memory fact your answer relies on, or that you saved or corrected",
            }),
        },
        ["answer", "factIds"]);

    public static readonly AgentRules Rules = new(AgentContract.MaxToolCalls, Answer, TypedAnswer: false);

    public static string FindPrompt =>
        "You are a project manager's assistant at Orrin Systems. Your memory is the Connapse memory space named "
        + $"\"{SpaceName}\"; read it with the memory tools. Answer the question from memory, then call the answer tool with a "
        + "short answer and, in factIds, the id of every fact the answer relies on. If memory does not hold the answer, "
        + $"say so and cite no facts. You may make at most {AgentContract.MaxToolCalls} tool calls before answering.";

    public static string SavePrompt =>
        "You are a project manager's assistant at Orrin Systems, and you keep its memory accurate. Your memory is the "
        + $"Connapse memory space named \"{SpaceName}\". When the user tells you something, check what memory already holds, "
        + "then save it with memory_remember if memory lacks it, fix the fact with memory_correct if its value changed, and "
        + $"change nothing if memory already holds it. Today is {Today}. When you are done, call the answer tool with a "
        + "one-line summary and, in factIds, the id of every fact you saved or corrected (none if you changed nothing). "
        + $"You may make at most {AgentContract.MaxToolCalls} tool calls before answering.";

    public async Task<int> RunAsync(Options options, CancellationToken ct)
    {
        if (!Arms.Contains(options.Arm))
            throw new ArgumentException($"--arm must be one of {string.Join(", ", Arms)}.");

        DatasetEntry entry = EvalManifest.Load(paths.ManifestPath).Datasets[Dataset];
        DatasetCache cache = new(paths.CacheRoot, http, paths.DatasetsRoot);
        await cache.EnsureAsync(Dataset, entry, allowUnpinned: false, ct);
        string root = cache.DirectoryFor(Dataset, entry);
        List<SeedFact> facts = ReadFacts(Path.Combine(root, "facts.jsonl"));
        List<AgentTask> all = [.. ReadTasks(Path.Combine(root, "tasks.jsonl")).Where(t => t.Split == options.Split)];
        List<AgentTask> find = Limit([.. all.Where(t => t.Type == "find")], options.LimitTasks);
        List<AgentTask> save = Limit([.. all.Where(t => t.Type == "save")], options.LimitTasks);

        SystemConfig config = SystemConfig.Load(paths.EvalRoot, "connapse", options.Config);
        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        await using ConnapseSearchSystem system = await ConnapseSearchSystem.StartAsync(config, paths.WebContentRoot, embeddings, log, null, ct);

        Guid reader = Guid.NewGuid();
        Seeded seeded = await SeedAsync(system.Services, reader, facts, ct);
        log.WriteLine($"{facts.Count} facts seeded into {SpaceName} ({seeded.Old.Count} with an older version)");

        JsonObject report = new()
        {
            ["config"] = options.Config,
            ["split"] = options.Split,
            ["model"] = options.Model,
            ["effort"] = options.Effort,
            ["repetitions"] = options.Repetitions,
            ["arm"] = options.Arm,
            ["facts"] = facts.Count,
        };
        if (options.Arm is "all" or "retrieval")
            report["retrieval"] = await RetrievalAsync(system.Services, reader, seeded, find, ct);

        List<string> runLines = [];
        if (options.Arm != "retrieval")
        {
            string agentDir = Path.GetFullPath(options.AgentDir ?? Path.Combine(Path.GetTempPath(), "connapse-memory-agent"));
            Directory.CreateDirectory(agentDir);
            MemorySearchEval.RequireNoProjectMemory(agentDir);
            await using ToolBridge bridge = new();
            await bridge.StartAsync(ct);
            ClaudeCliAgent agent = new(bridge, options.Model, options.Effort, agentDir, rules: Rules);

            if (options.Arm is "all" or "find")
            {
                McpToolbox tools = new(system.Services, McpToolbox.Reader(reader, "memory-agent-eval"), ReadTools);
                List<TaskResult> results = await RunJobsAsync(find, options, agent, async (task, token) =>
                {
                    AgentRun run = await agent.RunAsync(tools, FindPrompt, task.Question, token);
                    return (run, ScoreFind(task, run, seeded));
                }, ct);
                report["find"] = Section(results, HardKinds);
                runLines.AddRange(RunLines(results));
            }

            if (options.Arm is "all" or "save")
            {
                List<TaskResult> results = await RunJobsAsync(save, options, agent, async (task, token) =>
                {
                    // Each run writes, so each gets its own user and a freshly seeded space.
                    Guid user = Guid.NewGuid();
                    Seeded own = await SeedAsync(system.Services, user, facts, token);
                    McpToolbox tools = new(system.Services, Writer(user, "memory-agent-eval"), WriteTools);
                    AgentRun run = await agent.RunAsync(tools, SavePrompt, task.Question, token);
                    List<MemoryFact> versions = await VersionsAsync(system.Services, own.Space, token);
                    return (run, ScoreSave(task, run, own, versions));
                }, ct);
                report["save"] = Section(results, []);
                runLines.AddRange(RunLines(results));
            }

            report["subscription"] = new JsonObject
            {
                ["atFirstRun"] = agent.FirstLimit?.ToString(),
                ["atLastRun"] = agent.LastLimit?.ToString(),
            };
        }

        List<string> nonconforming = Nonconforming(options, find.Count, save.Count);
        report["conforming"] = nonconforming.Count == 0;
        report["diagnosticOnly"] = new JsonArray([.. nonconforming.Select(r => (JsonNode)r)]);

        string json = report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (options.OutPath is not null)
        {
            await File.WriteAllTextAsync(options.OutPath, json + "\n", ct);
            if (runLines.Count > 0)
                await File.WriteAllLinesAsync(Path.ChangeExtension(options.OutPath, ".runs.jsonl"), runLines, ct);
        }
        log.WriteLine(json);
        return 0;
    }

    private static List<AgentTask> Limit(List<AgentTask> tasks, int? limit) => limit is int n ? [.. tasks.Take(n)] : tasks;

    private async Task<List<TaskResult>> RunJobsAsync(IReadOnlyList<AgentTask> tasks, Options options, ClaudeCliAgent agent,
        Func<AgentTask, CancellationToken, Task<(AgentRun, TaskScore)>> run, CancellationToken ct)
    {
        List<(AgentTask Task, int Rep)> jobs = [.. tasks.SelectMany(t => Enumerable.Range(0, options.Repetitions).Select(r => (t, r)))];
        ConcurrentDictionary<(string, int), (AgentRun, TaskScore)> done = new();
        int finished = 0;
        await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = options.Workers, CancellationToken = ct }, async (job, token) =>
        {
            (AgentRun r, TaskScore s) = await run(job.Task, token);
            done[(job.Task.Id, job.Rep)] = (r, s);
            int n = Interlocked.Increment(ref finished);
            log.WriteLine($"[{n}/{jobs.Count}] {job.Task.Id} rep {job.Rep + 1}: {r.Outcome} score {s.Score} calls {r.Calls.Count}"
                + $"{(s.Note is null ? "" : $" ({s.Note})")}{(r.Error is null ? "" : $" ({r.Error})")} | {agent.LastLimit}");
        });
        return [.. tasks.Select(t => new TaskResult(t, [.. Enumerable.Range(0, options.Repetitions).Select(r => done[(t.Id, r)])]))];
    }

    /// <summary>An agent with an Admin token scoped to reading and writing knowledge, as the save tools require.</summary>
    public static ClaimsPrincipal Writer(Guid userId, string name) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
        new Claim(ClaimTypes.Name, name),
        new Claim(ClaimTypes.Role, "Admin"),
        new Claim("scope", "knowledge:read"),
        new Claim("scope", "knowledge:write"),
    ], "eval"));

    /// <summary>
    /// Makes the user's personal space and saves every fact through the MCP tools. A fact with history is saved with its
    /// old value first, then corrected as changed on the day it changed, so the old version stays in history.
    /// </summary>
    public static async Task<Seeded> SeedAsync(IServiceProvider services, Guid userId, IReadOnlyList<SeedFact> facts, CancellationToken ct)
    {
        McpToolbox tools = new(services, Writer(userId, "memory-agent-seed"), ["memory_space_create", "memory_remember", "memory_correct"]);
        JsonNode created = await CallAsync(tools, "memory_space_create", new
        {
            name = SpaceName,
            kind = "personal",
            description = "What a project manager keeps about Orrin Systems: projects, people, services and working rules",
        }, ct);
        Guid space = created["created"]!["id"]!.GetValue<Guid>();

        Dictionary<string, Guid> current = new(StringComparer.Ordinal);
        Dictionary<string, Guid> old = new(StringComparer.Ordinal);
        foreach (SeedFact f in facts)
        {
            if (f.History is { } h)
            {
                JsonNode first = await CallAsync(tools, "memory_remember",
                    new { subject = f.Subject, attribute = f.Attribute, value = h.Value, space = SpaceName, validFrom = h.ValidFrom }, ct);
                Guid oldId = first["remembered"]!["id"]!.GetValue<Guid>();
                JsonNode corrected = await CallAsync(tools, "memory_correct",
                    new { factId = oldId.ToString(), value = f.Value, kind = "changed", validFrom = h.ChangedOn }, ct);
                old[f.Key] = oldId;
                current[f.Key] = corrected["remembered"]!["id"]!.GetValue<Guid>();
            }
            else
            {
                JsonNode saved = await CallAsync(tools, "memory_remember",
                    new { subject = f.Subject, attribute = f.Attribute, value = f.Value, space = SpaceName }, ct);
                current[f.Key] = saved["remembered"]!["id"]!.GetValue<Guid>();
            }
        }

        Dictionary<Guid, Guid> chainOf = (await VersionsAsync(services, space, ct)).ToDictionary(v => v.Id, v => v.ChainId);
        Dictionary<string, Guid> chain = current.ToDictionary(c => c.Key, c => chainOf[c.Value], StringComparer.Ordinal);
        return new Seeded(space, current, old, chain);
    }

    private static async Task<JsonNode> CallAsync(McpToolbox tools, string name, object args, CancellationToken ct)
    {
        Dictionary<string, JsonElement> input = JsonSerializer.SerializeToElement(args).EnumerateObject()
            .Where(p => p.Value.ValueKind != JsonValueKind.Null)
            .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        string output = await tools.CallAsync(name, input, ct);
        return output.StartsWith("Error", StringComparison.Ordinal)
            ? throw new InvalidOperationException($"Seeding failed at {name}: {output}")
            : JsonNode.Parse(output)!;
    }

    /// <summary>Every version of every fact in the space, read straight from the store.</summary>
    public static async Task<List<MemoryFact>> VersionsAsync(IServiceProvider services, Guid space, CancellationToken ct)
    {
        const int page = 200;
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IMemoryStore store = scope.ServiceProvider.GetRequiredService<IMemoryStore>();
        List<MemoryFact> all = [];
        for (int skip = 0; ; skip += page)
        {
            IReadOnlyList<MemoryFact> batch = await store.QueryAsync(new MemoryQuery(space, History: true, Limit: page), skip, page, ct);
            all.AddRange(batch);
            if (batch.Count < page)
                return all;
        }
    }

    /// <summary>
    /// A find run scores 1 when it answered, cited a current version of a gold fact and no older version of any fact.
    /// An absent run scores 1 when it answered and cited nothing.
    /// </summary>
    public static TaskScore ScoreFind(AgentTask task, AgentRun run, Seeded seeded)
    {
        HashSet<Guid> cited = [.. run.Cited.Select(c => Guid.TryParse(c, out Guid g) ? g : Guid.Empty).Where(g => g != Guid.Empty)];
        HashSet<Guid> gold = [.. task.Gold.Select(k => seeded.Current[k])];
        bool answered = run.Outcome == AgentOutcome.Answered;
        bool citedGold = cited.Overlaps(gold);
        bool citedOld = cited.Overlaps(seeded.Old.Values);
        bool right = task.Kind == "absent" ? answered && run.Cited.Count == 0 : answered && citedGold && !citedOld;

        List<ToolTrace> reads = [.. run.Calls.Where(c => c.Name is "memory_recall" or "memory_timeline")];
        List<ToolTrace> recalls = [.. reads.Where(c => c.Name == "memory_recall")];
        bool Shows(ToolTrace c) => gold.Any(g => c.Output.Contains(g.ToString(), StringComparison.OrdinalIgnoreCase));
        return new TaskScore(right ? 1 : 0, new Dictionary<string, double>
        {
            ["sawGold"] = task.Kind == "absent" ? 0 : reads.Any(Shows) ? 1 : 0,
            ["firstRecallHit"] = task.Kind == "absent" ? 0 : recalls.Count > 0 && Shows(recalls[0]) ? 1 : 0,
            ["citedOld"] = citedOld ? 1 : 0,
            ["toolCalls"] = run.Calls.Count,
            ["recallCalls"] = recalls.Count,
        });
    }

    /// <summary>
    /// A save run is scored on what memory holds afterwards. A fact is live when it is neither ended nor corrected; a
    /// seeded fact changed when its seeded version is no longer live or another live version of it appeared.
    /// <list type="bullet">
    /// <item>new: no seeded fact changed, and one or two new live facts hold every expected word group.</item>
    /// <item>update: the target's seeded version is no longer live, no other seeded fact changed, and one or two new live facts hold every group.</item>
    /// <item>duplicate: nothing new was saved and no seeded fact changed but the target, whose live version, if it changed, still holds every group.</item>
    /// </list>
    /// A run that ended in error scores 0.
    /// </summary>
    public static TaskScore ScoreSave(AgentTask task, AgentRun run, Seeded seeded, IReadOnlyList<MemoryFact> versions)
    {
        List<MemoryFact> live = [.. versions.Where(v => v.SupersededAt is null && v.ValidTo is null)];
        HashSet<Guid> liveIds = [.. live.Select(v => v.Id)];
        HashSet<Guid> seedHeads = [.. seeded.Current.Values];
        HashSet<Guid> seedChains = [.. seeded.Chain.Values];

        HashSet<string> changed = [.. seeded.Current.Keys.Where(k =>
            !liveIds.Contains(seeded.Current[k]) || live.Any(v => v.ChainId == seeded.Chain[k] && v.Id != seeded.Current[k]))];
        List<MemoryFact> added = [.. live.Where(v => !seedHeads.Contains(v.Id))];
        List<MemoryFact> newChains = [.. added.Where(v => !seedChains.Contains(v.ChainId))];
        List<string> others = [.. changed.Where(k => k != task.Target)];
        bool targetChanged = task.Target is not null && changed.Contains(task.Target);
        bool sameChain = task.Target is not null && added.Any(v => v.ChainId == seeded.Chain[task.Target]);

        (bool right, string? note) = task.Kind switch
        {
            _ when run.Outcome == AgentOutcome.Error => (false, "error"),
            "new" => changed.Count > 0 ? (false, $"changed {string.Join(",", changed)}")
                : newChains.Count is < 1 or > 2 ? (false, $"{newChains.Count} new facts")
                : Holds(newChains, task.Expect) ? (true, null) : (false, "missing words"),
            "update" => !targetChanged ? (false, "target unchanged")
                : others.Count > 0 ? (false, $"changed {string.Join(",", others)}")
                : added.Count is < 1 or > 2 ? (false, $"{added.Count} new facts")
                : Holds(added, task.Expect) ? (true, null) : (false, "missing words"),
            "duplicate" => newChains.Count > 0 ? (false, $"{newChains.Count} new facts")
                : others.Count > 0 ? (false, $"changed {string.Join(",", others)}")
                : !targetChanged || Holds([.. live.Where(v => v.ChainId == seeded.Chain[task.Target!])], task.Expect) ? (true, null)
                : (false, "target lost its value"),
            _ => throw new InvalidOperationException($"Unknown save kind '{task.Kind}'."),
        };

        return new TaskScore(right ? 1 : 0, new Dictionary<string, double>
        {
            ["otherFactsChanged"] = others.Count > 0 ? 1 : 0,
            ["sameChain"] = sameChain ? 1 : 0,
            ["toolCalls"] = run.Calls.Count,
            ["recallCalls"] = run.Calls.Count(c => c.Name == "memory_recall"),
        }, note);
    }

    /// <summary>Whether the facts' subjects, attributes and values hold at least one word of every group, ignoring case.</summary>
    public static bool Holds(IEnumerable<MemoryFact> facts, IReadOnlyList<IReadOnlyList<string>> groups)
    {
        string text = string.Join(" ", facts.Select(f => $"{f.Subject} {f.Attribute} {f.Value}")).ToLowerInvariant();
        return groups.All(g => g.Any(w => text.Contains(w.ToLowerInvariant(), StringComparison.Ordinal)));
    }

    /// <summary>
    /// The retrieval arm: <c>memory_recall</c> called once per find task with the question as the query, the way an agent's
    /// first call often is. Reports hit@1, hit@10 and MRR per kind, how often an absent question returns anything, and the
    /// call's latency.
    /// </summary>
    public static async Task<JsonObject> RetrievalAsync(IServiceProvider services, Guid user, Seeded seeded, IReadOnlyList<AgentTask> tasks,
        CancellationToken ct)
    {
        McpToolbox tools = new(services, McpToolbox.Reader(user, "memory-agent-retrieval"), ["memory_recall"]);
        List<(AgentTask Task, int Rank, int Returned)> rows = [];
        List<double> millis = [];
        foreach (AgentTask task in tasks)
        {
            Dictionary<string, JsonElement> input = new(StringComparer.Ordinal)
            {
                ["space"] = JsonSerializer.SerializeToElement(SpaceName),
                ["query"] = JsonSerializer.SerializeToElement(task.Question),
                ["limit"] = JsonSerializer.SerializeToElement(10),
            };
            Stopwatch clock = Stopwatch.StartNew();
            string output = await tools.CallAsync("memory_recall", input, ct);
            millis.Add(clock.Elapsed.TotalMilliseconds);
            List<Guid> ids = output.StartsWith("Error", StringComparison.Ordinal) ? []
                : [.. (JsonNode.Parse(output)!["facts"]?.AsArray() ?? []).Select(f => f!["id"]!.GetValue<Guid>())];
            HashSet<Guid> gold = [.. task.Gold.Select(k => seeded.Current[k])];
            int rank = ids.FindIndex(gold.Contains) + 1;
            rows.Add((task, rank, ids.Count));
        }

        JsonObject kinds = [];
        foreach (IGrouping<string, (AgentTask Task, int Rank, int Returned)> g in rows.GroupBy(r => r.Task.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
            kinds[g.Key] = g.Key == "absent"
                ? new JsonObject { ["tasks"] = g.Count(), ["returnedAny"] = Round(g.Average(r => r.Returned > 0 ? 1.0 : 0)) }
                : RankGroup([.. g]);
        millis.Sort();
        return new JsonObject
        {
            ["tasks"] = rows.Count,
            ["answerable"] = RankGroup([.. rows.Where(r => r.Task.Kind != "absent")]),
            ["hard"] = RankGroup([.. rows.Where(r => HardKinds.Contains(r.Task.Kind))]),
            ["kinds"] = kinds,
            ["latencyMs"] = new JsonObject { ["p50"] = Round(Percentile(millis, 0.50)), ["p95"] = Round(Percentile(millis, 0.95)) },
            ["perTask"] = new JsonObject([.. rows.Select(r => KeyValuePair.Create(r.Task.Id, (JsonNode?)r.Rank))]),
        };
    }

    private static JsonObject RankGroup(IReadOnlyList<(AgentTask Task, int Rank, int Returned)> rows) => new()
    {
        ["tasks"] = rows.Count,
        ["hit@1"] = Round(rows.Select(r => r.Rank == 1 ? 1.0 : 0).DefaultIfEmpty(0).Average()),
        ["hit@10"] = Round(rows.Select(r => r.Rank is >= 1 and <= 10 ? 1.0 : 0).DefaultIfEmpty(0).Average()),
        ["mrr@10"] = Round(rows.Select(r => r.Rank is >= 1 and <= 10 ? 1.0 / r.Rank : 0).DefaultIfEmpty(0).Average()),
    };

    /// <summary>The nearest-rank percentile of sorted values.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    /// <summary>An arm's report: overall and per kind, the named group of kinds, per-task means for paired tests, outcomes and tokens.</summary>
    public static JsonObject Section(IReadOnlyList<TaskResult> results, IReadOnlyList<string> hardKinds)
    {
        JsonObject kinds = [];
        foreach (string kind in results.Select(r => r.Task.Kind).Distinct().Order(StringComparer.Ordinal))
            kinds[kind] = Group([.. results.Where(r => r.Task.Kind == kind)]);
        JsonObject section = new()
        {
            ["tasks"] = results.Count,
            ["all"] = Group(results),
            ["kinds"] = kinds,
        };
        if (hardKinds.Count > 0)
            section["hard"] = Group([.. results.Where(r => hardKinds.Contains(r.Task.Kind))]);
        section["perTask"] = new JsonObject([.. results.Select(r => KeyValuePair.Create(r.Task.Id, (JsonNode?)Round(r.Runs.Average(x => x.Score.Score))))]);
        section["outcomes"] = Outcomes(results.SelectMany(r => r.Runs));
        section["tokens"] = Tokens(results.SelectMany(r => r.Runs));
        return section;
    }

    /// <summary>The score and every measure, averaged per task and then over the tasks, so every task counts the same.</summary>
    private static JsonObject Group(IReadOnlyList<TaskResult> group)
    {
        JsonObject o = new()
        {
            ["tasks"] = group.Count,
            ["score"] = TaskMean(group, s => s.Score),
        };
        foreach (string measure in group.SelectMany(r => r.Runs).SelectMany(r => r.Score.Measures.Keys).Distinct().Order(StringComparer.Ordinal))
            o[measure] = TaskMean(group, s => s.Measures.GetValueOrDefault(measure));
        return o;
    }

    private static double TaskMean(IReadOnlyList<TaskResult> group, Func<TaskScore, double> measure) =>
        Round(group.Select(r => r.Runs.Select(x => measure(x.Score)).DefaultIfEmpty(0).Average()).DefaultIfEmpty(0).Average());

    private static JsonObject Outcomes(IEnumerable<(AgentRun Run, TaskScore Score)> runs)
    {
        JsonObject counts = [];
        foreach (IGrouping<AgentOutcome, (AgentRun Run, TaskScore Score)> g in runs.GroupBy(r => r.Run.Outcome).OrderBy(g => g.Key))
            counts[g.Key.ToString()] = g.Count();
        return counts;
    }

    /// <summary>Token totals, and what they would cost at API list price; the run itself is billed to the subscription.</summary>
    private static JsonObject Tokens(IEnumerable<(AgentRun Run, TaskScore Score)> runs)
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

    private static IEnumerable<string> RunLines(IReadOnlyList<TaskResult> results)
    {
        foreach (TaskResult r in results)
            for (int i = 0; i < r.Runs.Count; i++)
            {
                (AgentRun run, TaskScore score) = r.Runs[i];
                yield return new JsonObject
                {
                    ["task"] = r.Task.Id,
                    ["rep"] = i + 1,
                    ["outcome"] = run.Outcome.ToString(),
                    ["score"] = score.Score,
                    ["note"] = score.Note,
                    ["answer"] = run.Answer,
                    ["cited"] = new JsonArray([.. run.Cited.Select(c => (JsonNode?)c)]),
                    ["calls"] = new JsonArray([.. run.Calls.Select(c => (JsonNode?)new JsonObject
                    {
                        ["tool"] = c.Name,
                        ["input"] = JsonSerializer.SerializeToNode(c.Input),
                        ["output"] = c.Output,
                    })]),
                    ["error"] = run.Error,
                }.ToJsonString();
            }
    }

    /// <summary>Why a run is diagnostic only: anything short of the pre-registered run. Empty when it conforms.</summary>
    public static List<string> Nonconforming(Options options, int findTasks, int saveTasks)
    {
        List<string> reasons = [];
        if (options.Arm != "all")
            reasons.Add($"arm is {options.Arm}, not all");
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
        if (findTasks != RegisteredFindTasks)
            reasons.Add($"{findTasks} find tasks, not {RegisteredFindTasks}");
        if (saveTasks != RegisteredSaveTasks)
            reasons.Add($"{saveTasks} save tasks, not {RegisteredSaveTasks}");
        if (options.Repetitions != RegisteredRepetitions)
            reasons.Add($"{options.Repetitions} repetitions, not {RegisteredRepetitions}");
        return reasons;
    }

    private static double Round(double x) => Math.Round(x, 4);

    public static List<SeedFact> ReadFacts(string path) =>
        [.. File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()).Select(f => new SeedFact(
            (string)f["key"]!, (string)f["subject"]!, (string)f["attribute"]!, (string)f["value"]!,
            f["history"] is JsonObject h ? new SeedHistory((string)h["value"]!, (string)h["validFrom"]!, (string)h["changedOn"]!) : null))];

    public static List<AgentTask> ReadTasks(string path) =>
        [.. File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()).Select(t => new AgentTask(
            (string)t["id"]!, (string)t["type"]!, (string)t["kind"]!, (string)t["split"]!, (string)t["question"]!,
            [.. (t["gold"]?.AsArray() ?? []).Select(g => (string)g!)],
            (string?)t["target"],
            [.. (t["expect"]?.AsArray() ?? []).Select(g => (IReadOnlyList<string>)[.. g!.AsArray().Select(w => (string)w!)])]))];
}
