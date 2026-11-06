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
/// The CON-245 sorting bar measured after the agent decides (eval/pass-bars.md): each test-split note is handed to a
/// fresh agent that saves it through the real <c>memory_remember</c> and its closeness check. A restating note is
/// dropped when no save of it is stored; a new fact is kept when a save is stored, first try or with a reason.
/// </summary>
public sealed class MemorySortAgentEval(RepoPaths paths, HttpClient http, TextWriter log)
{
    public static readonly string[] Tools = [.. MemorySearchEval.OffTools, "memory_remember"];

    /// <summary>The pre-registered run sorts all 222 test-split notes.</summary>
    public const int RegisteredNotes = 222;

    public sealed record Options(string Config, string Model, string Effort, int? LimitNotes, int Workers, string? OutPath,
        string? AgentDir, double? StopAt);

    /// <summary>What one agent did with its note.</summary>
    public sealed record Sorted(bool Attempted, bool HeldFirst, bool Stored, bool SavedWithReason);

    public async Task<int> RunAsync(Options options, CancellationToken ct)
    {
        string agentDir = Path.GetFullPath(options.AgentDir ?? Path.Combine(Path.GetTempPath(), "connapse-memory-sort-agent"));
        Directory.CreateDirectory(agentDir);
        MemorySearchEval.RequireNoProjectMemory(agentDir);

        DatasetCache cache = new(paths.CacheRoot, http, paths.DatasetsRoot);
        EvalManifest manifest = EvalManifest.Load(paths.ManifestPath);
        string searchRoot = await MemoryNoveltyEval.RootAsync(cache, manifest, MemorySearchEval.Dataset, ct);
        string learnedRoot = await MemoryNoveltyEval.RootAsync(cache, manifest, MemoryLearnedEval.Dataset, ct);
        List<MemoryNoveltyEval.Note> notes =
        [
            .. MemoryNoveltyEval.PlantedNotes(File.ReadLines(Path.Combine(searchRoot, "memory.jsonl")), File.ReadLines(Path.Combine(searchRoot, "tasks.jsonl"))),
            .. MemoryNoveltyEval.LearnedNotes(File.ReadLines(Path.Combine(paths.RepoRoot, MemoryNoveltyEval.LearnedRuns))),
        ];
        notes = [.. notes.Where(n => n.Split == "test").Take(options.LimitNotes ?? int.MaxValue)];
        log.WriteLine($"{notes.Count} test notes: {notes.Count(n => n.Restates)} restating, {notes.Count(n => !n.Restates)} new");

        SystemConfig config = SystemConfig.Load(paths.EvalRoot, "connapse", options.Config);
        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        await using ConnapseSearchSystem system = await ConnapseSearchSystem.StartAsync(config, paths.WebContentRoot, embeddings, log, null, ct);
        Dictionary<string, Guid> containers = new(StringComparer.Ordinal)
        {
            ["planted"] = await MemoryNoveltyEval.IndexAsync(system, MemorySearchEval.Dataset, manifest, searchRoot, ct),
            ["learned"] = await MemoryNoveltyEval.IndexAsync(system, MemoryLearnedEval.Dataset, manifest, learnedRoot, ct),
        };

        await using ToolBridge bridge = new();
        await bridge.StartAsync(ct);
        ClaudeCliAgent agent = new(bridge, options.Model, options.Effort, agentDir) { StopAtSevenDay = options.StopAt };
        ConcurrentDictionary<string, AgentRun> runs = new(StringComparer.Ordinal);
        int done = 0;
        SubscriptionLimit? stoppedAt = null;
        try
        {
            await Parallel.ForEachAsync(notes, new ParallelOptions { MaxDegreeOfParallelism = options.Workers, CancellationToken = ct }, async (note, token) =>
            {
                Guid container = containers[note.Source];
                McpToolbox tools = new(system.Services, McpToolbox.Writer(Guid.NewGuid(), "memory-sort-agent-eval"), Tools, MemorySearchEval.Pinned);
                AgentRun run = await agent.RunAsync(tools, SystemPrompt(container), Question(note), token);
                runs[note.Id] = run;
                Sorted s = Classify(run);
                log.WriteLine($"[{Interlocked.Increment(ref done)}/{notes.Count}] {note.Id} ({(note.Restates ? "restating" : "new")}): {run.Outcome} " +
                    $"held {s.HeldFirst} stored {s.Stored} reason {s.SavedWithReason}{(run.Error is null ? "" : $" ({run.Error})")} | {agent.LastLimit}");
            });
        }
        catch (SubscriptionLimitException ex)
        {
            stoppedAt = ex.Limit;
            log.WriteLine($"stopped: {ex.Message}");
        }

        List<(MemoryNoveltyEval.Note Note, AgentRun Run)> sorted = [.. notes.Where(n => runs.ContainsKey(n.Id)).Select(n => (n, runs[n.Id]))];
        JsonObject report = Report(options, notes.Count, sorted, stoppedAt);
        report["subscription"] = new JsonObject
        {
            ["atFirstRun"] = agent.FirstLimit?.ToString(),
            ["atLastRun"] = agent.LastLimit?.ToString(),
        };
        string json = report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (options.OutPath is not null)
        {
            await File.WriteAllTextAsync(options.OutPath, json + "\n", ct);
            await File.WriteAllLinesAsync(Path.ChangeExtension(options.OutPath, ".runs.jsonl"), sorted.Select(r => RunLine(r.Note, r.Run)), ct);
        }
        log.WriteLine(json);
        return stoppedAt is not null ? 4 : (bool)report["gate"]!["passed"]! ? 0 : 3;
    }

    /// <summary>Parses <c>--stop-at</c>: a seven-day utilization between 0 and 1, or null when not given.</summary>
    public static double? StopLine(string? value)
    {
        if (value is null)
            return null;
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double line)
            || line <= 0 || line > 1)
            throw new ArgumentException($"--stop-at must be a number above 0 and at most 1, got '{value}'.");
        return line;
    }

    /// <summary>The agent's instructions: keep the note for later agents, and follow what the tools reply.</summary>
    public static string SystemPrompt(Guid container) =>
        $"You are an agent working with a company's internal documents, stored in the Connapse container with ID {container}. " +
        "Earlier in your work you worked out the note below, and you want to keep it for the agents who will later answer " +
        "questions in this container. Save it to this container's memory with memory_remember, using space \"" + container + "\". " +
        "You may search with search_knowledge and read a document with get_document. Follow what the tools tell you. When you " +
        "are done, call the answer tool with \"saved\" or \"not saved\" and no DocumentIds. " +
        $"You may make at most {AgentContract.MaxToolCalls} tool calls before answering.";

    public static string Question(MemoryNoveltyEval.Note note) =>
        $"The note to keep: subject \"{note.Subject}\", attribute \"{note.Attribute}\", value \"{note.Value}\".";

    /// <summary>Reads what the run's <c>memory_remember</c> calls did: held first, stored, and stored with a reason.</summary>
    public static Sorted Classify(AgentRun run)
    {
        List<ToolTrace> remembers = [.. run.Calls.Where(c => c.Name == "memory_remember")];
        List<ToolTrace> stored = [.. remembers.Where(c => MemoryLearnedEval.Saved(c.Output))];
        return new Sorted(
            remembers.Count > 0,
            remembers.Count > 0 && IsHeld(remembers[0].Output),
            stored.Count > 0,
            stored.Any(c => WhatIsNew(c) is { Length: > 0 }));
    }

    /// <summary>Whether a <c>memory_remember</c> came back held by the closeness check.</summary>
    public static bool IsHeld(string output) => output.StartsWith("Error: Not saved", StringComparison.Ordinal);

    public static string? WhatIsNew(ToolTrace call) =>
        call.Input.TryGetValue("whatIsNew", out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;

    public static JsonObject Report(Options options, int noteCount, IReadOnlyList<(MemoryNoveltyEval.Note Note, AgentRun Run)> runs, SubscriptionLimit? stoppedAt)
    {
        JsonObject all = Shares([.. runs]);
        bool passed = stoppedAt is null && runs.Count == RegisteredNotes && options.LimitNotes is null
            && options.Model == MemorySearchEval.RegisteredModel && options.Effort == MemorySearchEval.RegisteredEffort
            && options.Config == MemorySearchEval.RegisteredConfig
            && (double)all["restatingDropped"]! >= MemoryNoveltyEval.BarRestatingHeld
            && (double)all["newKept"]! >= MemoryNoveltyEval.MinNewKept;
        JsonObject outcomes = [];
        foreach (IGrouping<AgentOutcome, AgentOutcome> g in runs.Select(r => r.Run.Outcome).GroupBy(o => o).OrderBy(g => g.Key))
            outcomes[g.Key.ToString()] = g.Count();
        return new JsonObject
        {
            ["config"] = options.Config,
            ["model"] = options.Model,
            ["effort"] = options.Effort,
            ["notes"] = noteCount,
            ["sorted"] = runs.Count,
            ["stoppedAt"] = stoppedAt?.ToString(),
            ["all"] = all,
            ["bySource"] = new JsonObject(runs.GroupBy(r => r.Note.Source).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => KeyValuePair.Create(g.Key, (JsonNode?)Shares([.. g])))),
            ["outcomes"] = outcomes,
            ["tokens"] = MemorySearchEval.Tokens(runs.Select(r => (r.Run, new RunScore(0, false, false, false, 0, 0, 0, 0, 0, 0, 0)))),
            ["reasons"] = new JsonArray([.. runs.Select(r => r.Run.Calls.Where(c => c.Name == "memory_remember" && MemoryLearnedEval.Saved(c.Output))
                    .Select(WhatIsNew).FirstOrDefault(w => w is { Length: > 0 }) is { } w ? (r.Note, w) : default)
                .Where(x => x.w is not null).Take(8)
                .Select(x => (JsonNode?)new JsonObject { ["note"] = x.Note.Id, ["restates"] = x.Note.Restates, ["whatIsNew"] = x.w })]),
            ["gate"] = new JsonObject
            {
                ["restatingDroppedAtLeast"] = MemoryNoveltyEval.BarRestatingHeld,
                ["newKeptAtLeast"] = MemoryNoveltyEval.MinNewKept,
                ["registeredNotes"] = RegisteredNotes,
                ["passed"] = passed,
            },
        };
    }

    /// <summary>The restating notes dropped, the new facts kept, and how each kind got there.</summary>
    public static JsonObject Shares(IReadOnlyList<(MemoryNoveltyEval.Note Note, AgentRun Run)> runs)
    {
        List<(bool Restates, Sorted S)> s = [.. runs.Select(r => (r.Note.Restates, Classify(r.Run)))];
        JsonObject Counts(bool restates)
        {
            List<Sorted> kind = [.. s.Where(x => x.Restates == restates).Select(x => x.S)];
            return new JsonObject
            {
                ["notes"] = kind.Count,
                ["heldFirst"] = kind.Count(k => k.HeldFirst),
                ["stored"] = kind.Count(k => k.Stored),
                ["storedWithReason"] = kind.Count(k => k.SavedWithReason),
                ["noAttempt"] = kind.Count(k => !k.Attempted),
            };
        }
        int restating = s.Count(x => x.Restates);
        int fresh = s.Count - restating;
        return new JsonObject
        {
            ["restatingDropped"] = restating == 0 ? 0 : Math.Round((double)s.Count(x => x.Restates && !x.S.Stored) / restating, 4),
            ["newKept"] = fresh == 0 ? 1 : Math.Round((double)s.Count(x => !x.Restates && x.S.Stored) / fresh, 4),
            ["restating"] = Counts(true),
            ["new"] = Counts(false),
        };
    }

    private static string RunLine(MemoryNoveltyEval.Note note, AgentRun run)
    {
        Sorted s = Classify(run);
        return new JsonObject
        {
            ["note"] = note.Id,
            ["source"] = note.Source,
            ["restates"] = note.Restates,
            ["subject"] = note.Subject,
            ["attribute"] = note.Attribute,
            ["value"] = note.Value,
            ["outcome"] = run.Outcome.ToString(),
            ["answer"] = run.Answer,
            ["heldFirst"] = s.HeldFirst,
            ["stored"] = s.Stored,
            ["storedWithReason"] = s.SavedWithReason,
            ["calls"] = new JsonArray([.. run.Calls.Select(c => (JsonNode?)new JsonObject
            {
                ["tool"] = c.Name,
                ["input"] = JsonSerializer.SerializeToNode(c.Input),
                ["output"] = c.Name == "memory_remember" ? c.Output : null,
            })]),
            ["error"] = run.Error,
        }.ToJsonString();
    }
}
