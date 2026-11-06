using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Application;
using Connapse.Application.Memory;
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

    /// <summary>The registered protocol fixes search at its default 10 results in Hybrid mode, so the agent can't change either.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Pinned = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["search_knowledge"] = ["mode", "topK"],
    };

    public const string RegisteredConfig = "hybrid";
    public const string RegisteredModel = "claude-sonnet-5-5";
    public const string RegisteredEffort = "medium";

    public sealed record Options(string Config, string Split, string Model, string Effort, int Repetitions, int? LimitTasks, int Workers, string? OutPath,
        string? AgentDir = null, bool CheckSaves = false, string? ReuseOffRuns = null);

    public sealed record MemoryOp(string Op, string Id, string? Target, string? Subject, string? Attribute, string Value,
        string? ValidFrom, IReadOnlyList<string> Sources, string? Reason);

    /// <summary>What the closeness check did to the replayed memory (CON-245). <c>Rewritten</c> counts corrects whose target was dropped.</summary>
    public sealed record ReplayTally(int Saves, int Held, int Retried, int Dropped, int Rewritten, double MeanSaveMs);

    /// <summary>The reason a held undocumented note is saved again with, standing in for the agent that knows it.</summary>
    public const string ReplayReason = "The documents don't state this; it was learned outside them.";

    /// <summary>One arm's runs of one task, in repetition order.</summary>
    public sealed record TaskResult(SearchTask Task, IReadOnlyList<(AgentRun Run, RunScore Score)> Off, IReadOnlyList<(AgentRun Run, RunScore Score)> On);

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
        List<MemoryOp> ops = ReadMemory(Path.Combine(root, "memory.jsonl"));

        SystemConfig config = SystemConfig.Load(paths.EvalRoot, "connapse", options.Config);
        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        await using ConnapseSearchSystem system = await ConnapseSearchSystem.StartAsync(config, paths.WebContentRoot, embeddings, log, null, ct);
        IndexReport indexed = await system.IndexAsync(new EvalDataset(Dataset, entry.Version, [], corpus, [], new Qrels()), ct);
        (Guid container, IReadOnlyDictionary<string, string> docMap) = system.ContainerOf(Dataset);
        RequireCompleteIndex(indexed, corpus.Count, docMap.Count);

        Guid userId = Guid.NewGuid();
        ReplayTally? replay = null;
        if (options.CheckSaves)
            replay = await ReplayCheckedAsync(system.Services, container, docMap, ops,
                MemoryNoveltyEval.UndocumentedFacts(File.ReadLines(Path.Combine(root, "tasks.jsonl"))), userId, ct);
        else
            await ReplayAsync(system.Services, container, docMap, ops, new MemoryCaller(userId, null, "memory-search-eval"), ct);
        log.WriteLine($"{corpus.Count} documents indexed, {ops.Count} memory operations replayed");

        var principal = McpToolbox.Reader(userId, "memory-search-eval");
        McpToolbox off = new(system.Services, principal, OffTools, Pinned);
        McpToolbox on = new(system.Services, principal, OnTools, Pinned);
        DocumentIndex index = new(docMap, corpus.ToDictionary(d => d.Id, d => d.FileName!, StringComparer.Ordinal));
        string prompt = SystemPrompt(container);
        await using ToolBridge bridge = new();
        await bridge.StartAsync(ct);
        ClaudeCliAgent agent = new(bridge, options.Model, options.Effort, agentDir);

        // Each task's repetitions run Off then On back to back, so drift in the API hits both arms alike.
        bool[] arms = options.ReuseOffRuns is null ? [false, true] : [true];
        List<(SearchTask Task, int Rep, bool Memory)> jobs =
            [.. tasks.SelectMany(t => Enumerable.Range(0, options.Repetitions).SelectMany(r => arms.Select(memory => (t, r, memory))))];
        ConcurrentDictionary<(string, int, bool), (AgentRun, RunScore)> done = new();
        if (options.ReuseOffRuns is not null)
            foreach (KeyValuePair<(string Task, int Rep), (AgentRun Run, RunScore Score)> reused in
                ReadOffRuns(File.ReadLines(options.ReuseOffRuns), tasks, options.Repetitions))
                done[(reused.Key.Task, reused.Key.Rep, false)] = reused.Value;
        int finished = 0;
        await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = options.Workers, CancellationToken = ct }, async (job, token) =>
        {
            AgentRun run = await agent.RunAsync(job.Memory ? on : off, prompt, job.Task.Question, token);
            RunScore score = MemorySearchScoring.Score(job.Task, run, index);
            done[(job.Task.Id, job.Rep, job.Memory)] = (run, score);
            int n = Interlocked.Increment(ref finished);
            log.WriteLine($"[{n}/{jobs.Count}] {job.Task.Id} rep {job.Rep + 1} {(job.Memory ? "on " : "off")}: " +
                $"{run.Outcome} score {score.Score} calls {score.ToolCalls}{(run.Error is null ? "" : $" ({run.Error})")} | {agent.LastLimit}");
        });

        List<TaskResult> results = [.. tasks.Select(t => new TaskResult(t,
            [.. Enumerable.Range(0, options.Repetitions).Select(r => done[(t.Id, r, false)])],
            [.. Enumerable.Range(0, options.Repetitions).Select(r => done[(t.Id, r, true)])]))];

        JsonObject report = Report(options, results);
        if (replay is not null)
            report["replay"] = JsonSerializer.SerializeToNode(replay);
        report["subscription"] = new JsonObject
        {
            ["atFirstRun"] = agent.FirstLimit?.ToString(),
            ["atLastRun"] = agent.LastLimit?.ToString(),
        };
        string json = report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (options.OutPath is not null)
        {
            await File.WriteAllTextAsync(options.OutPath, json + "\n", ct);
            await File.WriteAllLinesAsync(Path.ChangeExtension(options.OutPath, ".runs.jsonl"), RunLines(results, index, arms), ct);
        }
        log.WriteLine(json);
        return (bool)report["gate"]!["passed"]! ? 0 : 3;
    }

    /// <summary>The same instructions for both arms; they name the container and never mention memory.</summary>
    public static string SystemPrompt(Guid container) =>
        $"You answer questions about a company's internal documents, stored in the Connapse container with ID {container}. " +
        "Use the tools to find the document that answers the question, and read a document in full with get_document " +
        "when a search result is not enough. When you are sure, call the answer tool with a short answer and the " +
        $"DocumentId of each document your answer relies on. You may make at most {AgentContract.MaxToolCalls} tool calls before answering.";

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
                : await memory.CorrectAsync(written[op.Target!].Fact, MemoryCorrection.Changed, fact, caller, true, ct);
            if (result.Fact is null)
                throw new InvalidOperationException($"{op.Id} was refused: {result.Error}");
            written[op.Id] = (result.Fact.Id, subject, attribute);
        }
    }

    /// <summary>
    /// The same memory, each save going through the real closeness check (CON-245). A correction also cites what its
    /// target cited, because a correction of a team fact must keep every source; CON-244's replay predates that rule.
    /// </summary>
    private static async Task<ReplayTally> ReplayCheckedAsync(IServiceProvider services, Guid container, IReadOnlyDictionary<string, string> docMap,
        IReadOnlyList<MemoryOp> ops, IReadOnlySet<string> undocumented, Guid userId, CancellationToken ct)
    {
        Dictionary<string, string> documentOf = docMap.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IMemoryService memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        RememberFact remember = scope.ServiceProvider.GetRequiredService<MemoryOperations>().Remember;
        OperationContext writer = new(McpToolbox.Writer(userId, "memory-search-eval"), OperationSurface.Mcp);
        MemoryCaller caller = new(userId, null, "memory-search-eval");
        Dictionary<Guid, IReadOnlyList<string>> cites = [];
        NewMemoryFact Fact(MemoryOp op, string subject, string attribute, IReadOnlyList<string> sources) => new(subject, attribute, op.Value, Date(op.ValidFrom), null,
            [.. sources.Select(s => new MemorySource(MemorySourceKind.Document, documentOf[s]))], op.Reason);

        return await ReplayThroughCheckAsync(ops, undocumented,
            async (op, subject, attribute, whatIsNew) =>
            {
                Attempt<MemoryWritten, MemoryStatus> saved = await remember.RunAsync(
                    writer, MemoryTarget.Space(container.ToString()), Fact(op, subject, attribute, op.Sources), whatIsNew, ct);
                if (saved.Status == MemoryStatus.CloseToDocuments)
                    return null;
                Guid id = saved.Result?.Fact.Id ?? throw new InvalidOperationException($"{op.Id} was refused: {saved.Status}");
                cites[id] = op.Sources;
                return id;
            },
            async (op, target, subject, attribute) =>
            {
                List<string> sources = [.. cites[target].Union(op.Sources, StringComparer.Ordinal)];
                MemoryWriteResult result = await memory.CorrectAsync(target, MemoryCorrection.Changed, Fact(op, subject, attribute, sources), caller, true, ct);
                Guid id = result.Fact?.Id ?? throw new InvalidOperationException($"{op.Id} was refused: {result.Error}");
                cites[id] = sources;
                return id;
            });
    }

    /// <summary>
    /// The pre-registered replay: a held remember is saved again with a reason only when its task labels it undocumented,
    /// and otherwise dropped; a correct whose target was dropped becomes a remember of the new value under the target's
    /// subject and attribute, checked the same way. <paramref name="remember"/> returns null when the save was held.
    /// </summary>
    public static async Task<ReplayTally> ReplayThroughCheckAsync(IReadOnlyList<MemoryOp> ops, IReadOnlySet<string> undocumented,
        Func<MemoryOp, string, string, string?, Task<Guid?>> remember, Func<MemoryOp, Guid, string, string, Task<Guid>> correct)
    {
        Dictionary<string, (Guid? Fact, string Subject, string Attribute)> seen = new(StringComparer.Ordinal);
        int saves = 0, held = 0, retried = 0, dropped = 0, rewritten = 0;
        System.Diagnostics.Stopwatch saving = new();

        async Task<Guid?> SaveAsync(MemoryOp op, string subject, string attribute, string? whatIsNew)
        {
            saves++;
            saving.Start();
            Guid? fact = await remember(op, subject, attribute, whatIsNew);
            saving.Stop();
            return fact;
        }

        foreach (MemoryOp op in ops)
        {
            bool isRemember = op.Op == "remember";
            (Guid? target, string subject, string attribute) = isRemember ? (null, op.Subject!, op.Attribute!) : seen[op.Target!];
            if (target is Guid stored)
            {
                seen[op.Id] = (await correct(op, stored, subject, attribute), subject, attribute);
                continue;
            }
            if (!isRemember)
                rewritten++;
            Guid? saved = await SaveAsync(op, subject, attribute, null);
            if (saved is null)
            {
                held++;
                if (undocumented.Contains(op.Id))
                {
                    retried++;
                    saved = await SaveAsync(op, subject, attribute, ReplayReason);
                }
                if (saved is null)
                    dropped++;
            }
            seen[op.Id] = (saved, subject, attribute);
        }
        return new ReplayTally(saves, held, retried, dropped, rewritten, saves == 0 ? 0 : Math.Round(saving.Elapsed.TotalMilliseconds / saves, 1));
    }

    /// <summary>
    /// CON-244's off runs as this run's off arm, paired by task and repetition. Those files keep tool inputs but not
    /// always outputs, so run recall, first-query MRR and tokens are unknown here and the report leaves them out.
    /// </summary>
    public static Dictionary<(string Task, int Rep), (AgentRun Run, RunScore Score)> ReadOffRuns(
        IEnumerable<string> runLines, IReadOnlyCollection<SearchTask> tasks, int repetitions)
    {
        Dictionary<string, SearchTask> byId = tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);
        Dictionary<(string, int), (AgentRun, RunScore)> runs = [];
        foreach (JsonObject line in runLines.Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()))
        {
            int rep = (int)line["rep"]! - 1;
            if ((string)line["arm"]! != "off" || rep >= repetitions || !byId.TryGetValue((string)line["task"]!, out SearchTask? task))
                continue;
            AgentOutcome outcome = Enum.Parse<AgentOutcome>((string)line["outcome"]!);
            string? answer = (string?)line["answer"];
            List<string> cited = [.. line["cited"]!.AsArray().Select(c => (string)c!)];
            List<ToolTrace> calls = [.. line["calls"]!.AsArray().Select(c => new ToolTrace((string)c!["tool"]!,
                c["input"].Deserialize<Dictionary<string, JsonElement>>() ?? [], ""))];
            bool answerRight = outcome == AgentOutcome.Answered && answer is not null
                && MemoryEval.Named(answer, task.Answer) >= 0
                && (task.StaleAnswer is null || MemoryEval.Named(answer, task.StaleAnswer) < 0);
            RunScore score = new((double)line["score"]!, task.Stale.Any(cited.Contains), answerRight, false, 0,
                calls.Count(c => c.Name == MemorySearchScoring.Search), calls.Count, 0, 0, 0, 0);
            runs[(task.Id, rep)] = (new AgentRun(outcome, answer, cited, calls, TokenUsage.Zero, 0, (string?)line["error"]), score);
        }
        foreach (SearchTask task in tasks)
            for (int rep = 0; rep < repetitions; rep++)
                if (!runs.ContainsKey((task.Id, rep)))
                    throw new InvalidOperationException($"No reusable off run for {task.Id} repetition {rep + 1}.");
        return runs;
    }

    /// <summary>
    /// CON-245's bars in <c>eval/pass-bars.md</c>. Planted: M gains at least 0.30 with its 95% interval above 0.
    /// Realistic: M's 95% interval stays above -0.10. Both: H's 90% interval stays above -0.10.
    /// </summary>
    public static bool MeetsCheckedBar(bool realistic, PairedComparison m, PairedComparison h90) =>
        h90.CiLow > -0.10 && (realistic ? m.CiLow > -0.10 : m.MeanDifference >= 0.30 && m.CiLow > 0);

    /// <summary>The pre-registered run: every test task, three repetitions per arm. Only this run can pass the gate.</summary>
    public const int RegisteredTasks = 112;
    public const int RegisteredRepetitions = 3;

    /// <summary>
    /// Stops before any agent runs when a document failed to index: memory could still hand the agent that
    /// document's id while search can never return it, which would favour the On arm.
    /// </summary>
    public static void RequireCompleteIndex(IndexReport report, int corpusDocuments, int mappedDocuments)
    {
        if (report.Failed > 0 || mappedDocuments != corpusDocuments)
            throw new InvalidOperationException(
                $"{report.Failed} of {corpusDocuments} documents failed to index and {mappedDocuments} have a Connapse id "
                + $"({string.Join(", ", report.FailedDocumentIds.Take(10))}); the arms are compared only on a complete corpus.");
    }

    /// <summary>
    /// Stops before any agent runs when a CLAUDE.md could reach the agents: Claude Code loads one from its working folder
    /// and every folder above it, so it would reach both arms.
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
        int incomplete = results.Count(r => r.Off.Count != options.Repetitions || r.On.Count != options.Repetitions);
        if (incomplete > 0)
            reasons.Add($"{incomplete} tasks are missing runs");
        return reasons;
    }

    public static JsonObject Report(Options options, IReadOnlyList<TaskResult> results, int registeredTasks = RegisteredTasks, bool realistic = false)
    {
        List<string> nonconforming = Nonconforming(options, results, registeredTasks);
        List<TaskResult> m = [.. results.Where(r => r.Task.InMemorySet)];
        List<TaskResult> h = [.. results.Where(r => !r.Task.InMemorySet)];
        PairedComparison? mCompare = Compare(m, s => s.Score);
        PairedComparison? hCompare90 = Compare(h, s => s.Score, 0.90);
        PairedComparison? allCompare = Compare(results, s => s.Score);
        Verdict? verdict = mCompare is not null && hCompare90 is not null && allCompare is not null
            ? MemorySearchScoring.Decide(mCompare, hCompare90, allCompare)
            : null;
        bool? barMet = options.CheckSaves && mCompare is not null && hCompare90 is not null ? MeetsCheckedBar(realistic, mCompare, hCompare90) : null;
        bool passed = options.CheckSaves ? barMet == true : verdict == Verdict.Keep;
        bool offReused = options.ReuseOffRuns is not null;

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
                ["runRecall"] = offReused ? null : Comparison(Compare(results, s => s.SawGold ? 1 : 0)),
                ["firstQueryMrr@10"] = offReused ? null : Comparison(Compare(results, s => s.FirstQueryMrr)),
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
                ["off"] = offReused ? null : Tokens(results.SelectMany(r => r.Off)),
                ["on"] = Tokens(results.SelectMany(r => r.On)),
            },
            ["examples"] = new JsonObject
            {
                ["memoryHelped"] = Examples(results, better: true),
                ["memoryHurt"] = Examples(results, better: false),
            },
            ["offRunsReusedFrom"] = options.ReuseOffRuns,
            ["gate"] = new JsonObject
            {
                ["checkedBar"] = options.CheckSaves ? (realistic ? "realistic" : "planted") : null,
                ["checkedBarMet"] = barMet,
                ["verdict"] = verdict is { } v ? MemorySearchScoring.Label(v) : "not scored (fewer than 2 tasks in a set)",
                ["memorySetMdeOver015"] = mCompare is null ? null : mCompare.MinimumDetectableEffect > 0.15,
                ["conforming"] = nonconforming.Count == 0,
                ["diagnosticOnly"] = new JsonArray([.. nonconforming.Select(r => (JsonNode)r)]),
                ["passed"] = passed && nonconforming.Count == 0,
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

    /// <summary>Token totals, and what they would cost at API list price; the run itself is billed to the subscription.</summary>
    internal static JsonObject Tokens(IEnumerable<(AgentRun Run, RunScore Score)> runs)
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

    private static IEnumerable<string> RunLines(IReadOnlyList<TaskResult> results, DocumentIndex index, bool[] arms)
    {
        foreach (TaskResult r in results)
            foreach ((string arm, IReadOnlyList<(AgentRun Run, RunScore Score)> runs) in arms.Select(memory => memory ? ("on", r.On) : ("off", r.Off)))
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
                            ["output"] = c.Output,
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
