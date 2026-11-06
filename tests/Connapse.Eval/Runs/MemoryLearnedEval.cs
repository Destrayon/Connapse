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
/// The memory-learned-v1 suite (CON-244, realistic arm): does memory an agent writes for itself make later, related
/// questions in the same container more reliable? Per repetition, in a fresh container:
/// <list type="number">
/// <item><b>Session 1</b>: agents answer the session-1 questions by searching, and save what they learn with the memory tools.</item>
/// <item>The late documents, each replacing an older policy, are uploaded.</item>
/// <item><b>Session 2</b>: fresh agents answer the related session-2 questions <b>Off</b> (search only) and <b>On</b>
/// (search plus <c>memory_recall</c> over what session 1 wrote), with the memory-search prompt, which never mentions memory.</item>
/// </list>
/// Only session 2 is scored, by the memory-search rules; the bar is in <c>eval/pass-bars.md</c>, written before any scored run.
/// </summary>
public sealed class MemoryLearnedEval(RepoPaths paths, HttpClient http, TextWriter log)
{
    public const string Dataset = "memory-learned";

    public static readonly string[] LearnTools = [.. MemorySearchEval.OnTools, "memory_remember", "memory_correct"];
    public static readonly string[] WriteTools = ["memory_remember", "memory_correct"];

    /// <summary>The pre-registered run scores the 104 session-2 test tasks.</summary>
    public const int RegisteredTasks = 104;

    public sealed record LearnedTask(SearchTask Task, string Family, int Session);

    public async Task<int> RunAsync(MemorySearchEval.Options options, CancellationToken ct)
    {
        string agentDir = Path.GetFullPath(options.AgentDir ?? Path.Combine(Path.GetTempPath(), "connapse-memory-learned-agent"));
        Directory.CreateDirectory(agentDir);
        MemorySearchEval.RequireNoProjectMemory(agentDir);
        RunCheckpoint checkpoint = new(RunCheckpoint.PathFor(options.OutPath));
        checkpoint.RequireSame("conditions", options.Conditions);
        MemoryGate gate = MemoryGate.ForThisMachine(options.MinFreeGb, log);
        await gate.WaitForRoomAsync(ct);

        DatasetEntry entry = EvalManifest.Load(paths.ManifestPath).Datasets[Dataset];
        DatasetCache cache = new(paths.CacheRoot, http, paths.DatasetsRoot);
        await cache.EnsureAsync(Dataset, entry, allowUnpinned: false, ct);
        string root = cache.DirectoryFor(Dataset, entry);
        List<LearnedTask> tasks = Select(ReadTasks(Path.Combine(root, "tasks.jsonl")), options.Split, options.LimitTasks);
        List<LearnedTask> first = [.. tasks.Where(t => t.Session == 1)];
        List<LearnedTask> second = [.. tasks.Where(t => t.Session == 2)];
        List<EvalDocument> corpus = MemorySearchEval.ReadCorpus(Path.Combine(root, "corpus.jsonl"));
        List<EvalDocument> late = MemorySearchEval.ReadCorpus(Path.Combine(root, "late.jsonl"));
        Dictionary<string, string> fileNames = corpus.Concat(late).ToDictionary(d => d.Id, d => d.FileName!, StringComparer.Ordinal);

        SystemConfig config = SystemConfig.Load(paths.EvalRoot, "connapse", options.Config);
        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        await using ConnapseSearchSystem system = await ConnapseSearchSystem.StartAsync(config, paths.WebContentRoot, embeddings, log, null, ct);
        await using ToolBridge bridge = new();
        await bridge.StartAsync(ct);
        ClaudeCliAgent agent = new(bridge, options.Model, options.Effort, agentDir) { Gate = gate };
        ParallelOptions parallel = new() { MaxDegreeOfParallelism = options.Workers, CancellationToken = ct };

        ConcurrentDictionary<(string, int), (AgentRun, RunScore)> learned = new();
        ConcurrentDictionary<(string, int, bool), (AgentRun, RunScore)> answered = new();
        bool[] arms = options.ReuseOffRuns is null ? [false, true] : [true];
        if (options.ReuseOffRuns is not null)
            foreach (KeyValuePair<(string Task, int Rep), (AgentRun Run, RunScore Score)> off in MemorySearchEval.ReadOffRuns(
                File.ReadLines(options.ReuseOffRuns), [.. second.Select(t => t.Task)], options.Repetitions))
                answered[(off.Key.Task, off.Key.Rep, false)] = off.Value;
        List<string> lines = [];
        // Session 2 reads what session 1 saved into that repetition's container, which a killed run takes with it. So a
        // repetition is kept whole once it finishes, and a repetition cut short runs again from its first task.
        DocumentIndex noDocuments = new(new Dictionary<string, string>(), new Dictionary<string, string>());
        for (int rep = 0; rep < options.Repetitions; rep++)
        {
            if (checkpoint.HasMark(RepetitionDone(rep)))
            {
                foreach (LearnedTask t in first)
                    learned[(t.Task.Id, rep)] = checkpoint.Get(RunKey("learn", t, rep));
                foreach (LearnedTask t in second)
                    foreach (bool memory in arms)
                        answered[(t.Task.Id, rep, memory)] = checkpoint.Get(RunKey(memory ? "on" : "off", t, rep));
                lines.AddRange(first.Select(t => RunLine(t, rep, "learn", learned[(t.Task.Id, rep)], noDocuments)));
                foreach (LearnedTask t in second)
                    foreach (bool memory in arms)
                        lines.Add(RunLine(t, rep, memory ? "on" : "off", answered[(t.Task.Id, rep, memory)], noDocuments));
                log.WriteLine($"rep {rep + 1}: kept from the checkpoint");
                continue;
            }

            // A fresh container and user per repetition, so no repetition sees another's memory.
            await gate.WaitForRoomAsync(ct);
            string name = $"{Dataset}-r{rep + 1}";
            IndexReport indexed = await system.IndexAsync(new EvalDataset(name, entry.Version, [], corpus, [], new Qrels()), ct);
            (Guid container, IReadOnlyDictionary<string, string> docMap) = system.ContainerOf(name);
            MemorySearchEval.RequireCompleteIndex(indexed, corpus.Count, docMap.Count);
            Guid userId = Guid.NewGuid();

            McpToolbox learn = new(system.Services, McpToolbox.Writer(userId, "memory-learned-eval"), LearnTools, MemorySearchEval.Pinned);
            DocumentIndex earlyIndex = new(docMap, fileNames);
            string learnPrompt = LearnPrompt(container, options.CheckSaves);
            int n1 = 0;
            int r = rep;
            await Parallel.ForEachAsync(first, parallel, async (t, token) =>
            {
                AgentRun run = await agent.RunAsync(learn, learnPrompt, t.Task.Question, token);
                RunScore score = MemorySearchScoring.Score(t.Task, run, earlyIndex);
                learned[(t.Task.Id, r)] = (run, score);
                log.WriteLine($"[rep {r + 1} session 1 {Interlocked.Increment(ref n1)}/{first.Count}] {t.Task.Id}: {run.Outcome} score {score.Score} " +
                    $"writes {Writes(run).Count} calls {score.ToolCalls}{(run.Error is null ? "" : $" ({run.Error})")} | {agent.LastLimit}");
            });

            IndexReport lateIndexed = await system.IndexAsync(new EvalDataset($"{name}-late", entry.Version, [], late, [], new Qrels()),
                IngestionWait.ThrowOnStall, ct, into: container);
            (_, IReadOnlyDictionary<string, string> lateMap) = system.ContainerOf($"{name}-late");
            MemorySearchEval.RequireCompleteIndex(lateIndexed, late.Count, lateMap.Count);
            Dictionary<string, string> allDocs = docMap.Concat(lateMap).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            DocumentIndex index = new(allDocs, fileNames);
            log.WriteLine($"rep {rep + 1}: {corpus.Count} documents, then {late.Count} late documents indexed");

            var reader = McpToolbox.Reader(userId, "memory-learned-eval");
            McpToolbox off = new(system.Services, reader, MemorySearchEval.OffTools, MemorySearchEval.Pinned);
            McpToolbox on = new(system.Services, reader, MemorySearchEval.OnTools, MemorySearchEval.Pinned);
            string prompt = MemorySearchEval.SystemPrompt(container);
            List<(LearnedTask Task, bool Memory)> jobs = [.. second.SelectMany(t => arms.Select(memory => (t, memory)))];
            int n2 = 0;
            await Parallel.ForEachAsync(jobs, parallel, async (job, token) =>
            {
                AgentRun run = await agent.RunAsync(job.Memory ? on : off, prompt, job.Task.Task.Question, token);
                RunScore score = MemorySearchScoring.Score(job.Task.Task, run, index);
                answered[(job.Task.Task.Id, r, job.Memory)] = (run, score);
                log.WriteLine($"[rep {r + 1} session 2 {Interlocked.Increment(ref n2)}/{jobs.Count}] {job.Task.Task.Id} {(job.Memory ? "on " : "off")}: " +
                    $"{run.Outcome} score {score.Score} calls {score.ToolCalls}{(run.Error is null ? "" : $" ({run.Error})")} | {agent.LastLimit}");
            });

            lines.AddRange(first.Select(t => RunLine(t, rep, "learn", learned[(t.Task.Id, rep)], earlyIndex)));
            foreach (LearnedTask t in second)
                foreach (bool memory in arms)
                    lines.Add(RunLine(t, rep, memory ? "on" : "off", answered[(t.Task.Id, rep, memory)], index));

            foreach (LearnedTask t in first)
                checkpoint.Save(RunKey("learn", t, rep), learned[(t.Task.Id, rep)].Item1, learned[(t.Task.Id, rep)].Item2, earlyIndex);
            foreach (LearnedTask t in second)
                foreach (bool memory in arms)
                    checkpoint.Save(RunKey(memory ? "on" : "off", t, rep), answered[(t.Task.Id, rep, memory)].Item1, answered[(t.Task.Id, rep, memory)].Item2, index);
            checkpoint.Mark(RepetitionDone(rep));
        }

        List<MemorySearchEval.TaskResult> results = [.. second.Select(t => new MemorySearchEval.TaskResult(t.Task,
            [.. Enumerable.Range(0, options.Repetitions).Select(r => answered[(t.Task.Id, r, false)])],
            [.. Enumerable.Range(0, options.Repetitions).Select(r => answered[(t.Task.Id, r, true)])]))];

        JsonObject report = MemorySearchEval.Report(options, results, RegisteredTasks, realistic: true);
        report["session1"] = Session1([.. first.SelectMany(t => Enumerable.Range(0, options.Repetitions).Select(r => (t, learned[(t.Task.Id, r)])))]);
        if (options.CheckSaves)
            report["saveCheck"] = SaveCheck([.. first.SelectMany(t => Enumerable.Range(0, options.Repetitions).Select(r => learned[(t.Task.Id, r)].Item1))]);
        report["memoryGuard"] = gate.Report(checkpoint.Restored);
        report["subscription"] = new JsonObject
        {
            ["atFirstRun"] = agent.FirstLimit?.ToString(),
            ["atLastRun"] = agent.LastLimit?.ToString(),
        };
        string json = report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (options.OutPath is not null)
        {
            await File.WriteAllTextAsync(options.OutPath, json + "\n", ct);
            await File.WriteAllLinesAsync(Path.ChangeExtension(options.OutPath, ".runs.jsonl"), lines, ct);
        }
        log.WriteLine(json);
        return (bool)report["gate"]!["passed"]! ? 0 : 3;
    }

    private static string RepetitionDone(int rep) => $"repetition {rep} done";

    private static string RunKey(string arm, LearnedTask t, int rep) => $"{arm}|{t.Task.Id}|{rep}";

    /// <summary>
    /// Session 1's instructions: answer, and save what would help with related questions later. With
    /// <paramref name="saveInContainer"/> saves name the container as their space, so the closeness check covers them (CON-245).
    /// </summary>
    public static string LearnPrompt(Guid container, bool saveInContainer = false) =>
        $"You answer questions about a company's internal documents, stored in the Connapse container with ID {container}. " +
        "Use the tools to find the document that answers the question, and read a document in full with get_document " +
        "when a search result is not enough. Other agents will later answer related questions in this container and can " +
        $"recall what you save. Before you answer, save with memory_remember{(saveInContainer ? $", using space \"{container}\"," : "")} what you worked out that would help someone " +
        "answer related questions about these documents, citing the documents it came from, and use memory_correct when " +
        "something you recalled turns out to be wrong. When you are sure, call the answer tool with a short answer and the " +
        $"DocumentId of each document your answer relies on. You may make at most {AgentContract.MaxToolCalls} tool calls before answering.";

    /// <summary>
    /// The tasks of <paramref name="split"/>, limited to the first <paramref name="limitFamilies"/> families that have a
    /// session-2 question, with their session-1 questions.
    /// </summary>
    public static List<LearnedTask> Select(IReadOnlyList<LearnedTask> tasks, string split, int? limitFamilies)
    {
        List<LearnedTask> inSplit = [.. tasks.Where(t => t.Task.Split == split)];
        if (limitFamilies is not int limit)
            return inSplit;
        HashSet<string> kept = [.. inSplit.Where(t => t.Session == 2).Select(t => t.Family).Distinct().Take(limit)];
        return [.. inSplit.Where(t => kept.Contains(t.Family))];
    }

    /// <summary>
    /// The memory writes a run saved, each with whether it cited at least one source. A refused or invalid write
    /// returns an error instead of a <c>remembered</c> fact, and isn't counted.
    /// </summary>
    public static List<bool> Writes(AgentRun run) =>
        [.. run.Calls.Where(c => WriteTools.Contains(c.Name, StringComparer.Ordinal) && Saved(c.Output))
            .Select(c => c.Input.TryGetValue("sources", out JsonElement s) && s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 0)];

    internal static bool Saved(string output)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(output);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("remembered", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static JsonObject Session1(IReadOnlyList<(LearnedTask Task, (AgentRun Run, RunScore Score) Result)> runs)
    {
        List<List<bool>> writes = [.. runs.Select(r => Writes(r.Result.Run))];
        JsonObject outcomes = [];
        foreach (IGrouping<AgentOutcome, AgentOutcome> g in runs.Select(r => r.Result.Run.Outcome).GroupBy(o => o).OrderBy(g => g.Key))
            outcomes[g.Key.ToString()] = g.Count();
        return new JsonObject
        {
            ["runs"] = runs.Count,
            ["meanScore"] = runs.Count == 0 ? 0 : Math.Round(runs.Average(r => r.Result.Score.Score), 4),
            ["outcomes"] = outcomes,
            ["memoryWrites"] = writes.Sum(w => w.Count),
            ["writesCitingSources"] = writes.Sum(w => w.Count(s => s)),
            ["runsThatWrote"] = writes.Count(w => w.Count > 0),
            ["tokens"] = MemorySearchEval.Tokens(runs.Select(r => r.Result)),
        };
    }

    /// <summary>What the closeness check did to session 1's saves: how many were held, and the reasons agents gave to save again.</summary>
    public static JsonObject SaveCheck(IReadOnlyList<AgentRun> runs)
    {
        List<ToolTrace> saves = [.. runs.SelectMany(r => r.Calls).Where(c => c.Name == "memory_remember")];
        List<string> reasons = [.. saves.Where(c => Saved(c.Output)).Select(MemorySortAgentEval.WhatIsNew).Where(r => r is { Length: > 0 }).Select(r => r!)];
        return new JsonObject
        {
            ["saveCalls"] = saves.Count,
            ["stored"] = saves.Count(c => Saved(c.Output)),
            ["held"] = saves.Count(c => MemorySortAgentEval.IsHeld(c.Output)),
            ["storedWithReason"] = reasons.Count,
            ["runsWithAHeldSave"] = runs.Count(r => r.Calls.Any(c => c.Name == "memory_remember" && MemorySortAgentEval.IsHeld(c.Output))),
            ["reasons"] = new JsonArray([.. reasons.Select(r => (JsonNode?)r)]),
        };
    }

    private static string RunLine(LearnedTask t, int rep, string arm, (AgentRun Run, RunScore Score) result, DocumentIndex index) =>
        new JsonObject
        {
            ["task"] = t.Task.Id,
            ["family"] = t.Family,
            ["session"] = t.Session,
            ["arm"] = arm,
            ["rep"] = rep + 1,
            ["outcome"] = result.Run.Outcome.ToString(),
            ["score"] = result.Score.Score,
            ["answer"] = result.Run.Answer,
            ["cited"] = new JsonArray([.. result.Run.Cited.Select(c => (JsonNode?)(index.Resolve(c) ?? c))]),
            ["calls"] = new JsonArray([.. result.Run.Calls.Select(c => (JsonNode?)new JsonObject
            {
                ["tool"] = c.Name,
                ["input"] = JsonSerializer.SerializeToNode(c.Input),
                ["output"] = WriteTools.Contains(c.Name, StringComparer.Ordinal) ? c.Output : null,
            })]),
            ["error"] = result.Run.Error,
        }.ToJsonString();

    public static List<LearnedTask> ReadTasks(string path)
    {
        List<SearchTask> tasks = MemorySearchEval.ReadTasks(path);
        List<JsonObject> rows = [.. File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject())];
        return [.. tasks.Zip(rows, (t, row) => new LearnedTask(t, (string)row["family"]!, (int)row["session"]!))];
    }
}
