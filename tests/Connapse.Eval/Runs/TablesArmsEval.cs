using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Eval.Cli;
using Connapse.Eval.Datasets;
using Connapse.Eval.Model;
using Connapse.Eval.Runs.MemorySearch;
using Connapse.Eval.Systems;
using MathNet.Numerics.Statistics;

namespace Connapse.Eval.Runs;

/// <summary>One way for an agent to answer table questions: the MCP tools it gets.</summary>
public sealed record TableArm(string Name, IReadOnlyList<string> Tools)
{
    /// <summary>Arm S: the four structured table tools, plus search.</summary>
    public static readonly TableArm Structured = new("structured",
        ["table_list", "table_describe", "table_read", "table_aggregate", .. MemorySearchEval.SearchTools]);

    /// <summary>
    /// Every arm. The free-form query arms (CON-327, CON-336) were removed with their tools in CON-345; see
    /// <c>docs/research/con-345-free-form-table-tools-removed.md</c>.
    /// </summary>
    public static readonly IReadOnlyList<TableArm> All = [Structured];

    /// <summary>The arms named on the command line, in the order given; all of them when none are named.</summary>
    public static IReadOnlyList<TableArm> Parse(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
            return All;
        List<TableArm> arms = [];
        foreach (string name in names)
        {
            TableArm arm = All.SingleOrDefault(a => a.Name == name)
                ?? throw new ArgumentException($"No arm is named '{name}'; the arms are {string.Join(", ", All.Select(a => a.Name))}.");
            if (!arms.Contains(arm))
                arms.Add(arm);
        }
        return arms;
    }

    /// <summary>The tools an arm needs that this build's MCP server doesn't have.</summary>
    public IReadOnlyList<string> Missing()
    {
        List<string> missing = [];
        foreach (string tool in Tools)
        {
            try
            {
                McpToolbox.Find(tool);
            }
            catch (ArgumentException)
            {
                missing.Add(tool);
            }
        }
        return missing;
    }
}

/// <summary>One run of one question by one arm.</summary>
public sealed record ArmRun(TableQuestion Question, string Arm, int Run, string Asked, AgentRun Agent, TableGrade Grade, TimeSpan Wall)
{
    /// <summary>Every tool call before the answer, refused queries and tool errors included.</summary>
    public int ToolCalls => Agent.Calls.Count;

    public int SearchCalls => Agent.Calls.Count(c => MemorySearchEval.SearchTools.Contains(c.Name));

    /// <summary>An answer given and graded wrong; running out of calls or giving no answer is not confident.</summary>
    public bool ConfidentWrong => Agent.Outcome == AgentOutcome.Answered && !Grade.Correct;
}

/// <summary>
/// CON-327: an agent with one arm's table tools on the tables-v1 questions (or CON-336's flexibility set). The data,
/// agent, prompt and grading are the rag setup's (<see cref="TablesRagEval"/>); only the tools differ. Each question gets
/// <see cref="Options.Runs"/> runs per arm, interleaved, and counts as right for an arm when most of its runs are right.
/// </summary>
public sealed class TablesArmsEval(RepoPaths paths, HttpClient http, TextWriter log)
{
    /// <summary>CON-336's bar: one attempt per question per arm (CON-327's three runs per arm came before it).</summary>
    public const int RegisteredRuns = 1;

    /// <summary>The question sets an arms run can ask: tables-v1, or CON-336's flexibility set.</summary>
    public const string TablesV1Set = "tables-v1";

    public const string FlexSet = "flex";

    /// <summary>The flexibility set's test questions: seven families of ten.</summary>
    public const int RegisteredFlexTest = 70;

    /// <param name="Base">The data, split, model and slice, as the rag setup takes them; its mixed questions are not asked.</param>
    /// <param name="Set"><see cref="TablesV1Set"/> or <see cref="FlexSet"/>.</param>
    /// <param name="MinFreeMemoryBytes">Each run waits until the machine has this much free memory; null never waits.</param>
    /// <param name="Resume">Keep the runs already in <c>&lt;out&gt;.runs.jsonl</c> and ask only the ones it lacks.</param>
    public sealed record Options(TablesRagEval.Options Base, IReadOnlyList<TableArm> Arms, int Runs, string Set = TablesV1Set,
        long? MinFreeMemoryBytes = null, bool Resume = false);

    /// <summary>
    /// The runs a cut-short journal already holds, by question, arm and zero-based run number, each with its line. When a
    /// run appears twice the later line wins, as the journal appends in finishing order.
    /// </summary>
    public static Dictionary<(string Id, string Arm, int Run), string> SavedRuns(IEnumerable<string> lines)
    {
        Dictionary<(string Id, string Arm, int Run), string> saved = [];
        foreach (string line in lines.Where(l => l.Length > 0))
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement r = doc.RootElement;
            saved[(r.GetProperty("id").GetString()!, r.GetProperty("arm").GetString()!, r.GetProperty("run").GetInt32() - 1)] = line;
        }
        return saved;
    }

    /// <summary>The test questions the bar expects from a set; anything else throws, so a typo can't run an unnamed set.</summary>
    public static int RegisteredTestOf(string set) => set switch
    {
        TablesV1Set => TablesRagEval.RegisteredTest,
        FlexSet => RegisteredFlexTest,
        _ => throw new ArgumentException($"No question set is named '{set}'; the sets are {TablesV1Set}, {FlexSet}."),
    };

    public async Task<int> RunAsync(Options options, CancellationToken ct)
    {
        RegisteredTestOf(options.Set);
        foreach (TableArm arm in options.Arms)
            if (arm.Missing() is { Count: > 0 } missing)
            {
                log.WriteLine($"Arm {arm.Name} needs {string.Join(", ", missing)}, which this build's MCP server doesn't have.");
                return 2;
            }

        TablesRagEval.Options b = options.Base;
        string agentDir = Path.GetFullPath(b.AgentDir ?? Path.Combine(Path.GetTempPath(), "connapse-tables-arms-agent"));
        Directory.CreateDirectory(agentDir);
        MemorySearchEval.RequireNoProjectMemory(agentDir);
        if (OperatingSystem.IsWindows())
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;

        SystemConfig config = SystemConfig.Load(paths.EvalRoot, "connapse", b.Config);
        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        await using ConnapseSearchSystem system = await ConnapseSearchSystem.StartAsync(config, paths.WebContentRoot, embeddings, log, null, ct);
        if (b.StallMinutes is int stallMinutes)
            system.StallTimeout = TimeSpan.FromMinutes(stallMinutes);

        TablesRagEval setup = new(paths, http, log);
        Dictionary<int, DateTime> seen = [];
        List<TableQuestion> questions;
        Guid container;
        Guid restricted = Guid.Empty;
        if (options.Set == FlexSet)
        {
            // The flexibility set's six files, uploaded once into one container under their own names.
            string flexRoot = Path.Combine(paths.DatasetsRoot, TablesRagEval.Dataset, FlexSet);
            questions = TablesRagEval.Select(TableQuestion.ReadAll(Path.Combine(flexRoot, "questions.jsonl")).Where(q => q.Split == b.Split), b);
            const string Name = "tables-flex";
            await setup.IndexAsync(system, Name, TablesNestedEval.Files(flexRoot, FlexSet), null, ct);
            container = system.ContainerOf(Name).ContainerId;
        }
        else
        {
            DatasetEntry entry = EvalManifest.Load(paths.ManifestPath).Datasets[TablesRagEval.Dataset];
            DatasetCache cache = new(paths.CacheRoot, http, paths.DatasetsRoot);
            await cache.EnsureAsync(TablesRagEval.Dataset, entry, allowUnpinned: false, ct);
            string root = cache.DirectoryFor(TablesRagEval.Dataset, entry);

            questions = TablesRagEval.Select(TableQuestion.ReadAll(Path.Combine(root, "questions.jsonl"))
                .Where(q => q.Split == b.Split), b);
            List<string> tableFiles = [.. entry.Files.Select(f => f.Name).Where(n => n.EndsWith(".csv", StringComparison.Ordinal)
                && !n.StartsWith("linked-", StringComparison.Ordinal) && TablesRagEval.InSlice(b.Tables, n))];

            // The same containers the rag setup builds: load 1, then load 2 over it, then the restricted user's view.
            await setup.IndexAsync(system, TablesRagEval.Dataset, TablesRagEval.Corpus(root, tableFiles, TablesRagEval.Loads[0]), null, ct);
            container = system.ContainerOf(TablesRagEval.Dataset).ContainerId;
            seen[1] = DateTime.UtcNow;
            await setup.IndexAsync(system, TablesRagEval.Dataset + "-load2", [TablesRagEval.TableDocument(root, TablesRagEval.Loads[1])], container, ct);
            seen[2] = DateTime.UtcNow;
            log.WriteLine($"inventory load 1 seen {TableQuestion.FormatSeen(seen[1])}, load 2 seen {TableQuestion.FormatSeen(seen[2])}");

            if (questions.Any(q => q.Restricted))
            {
                await setup.IndexAsync(system, TablesRagEval.Dataset + "-restricted",
                    [.. TablesRagEval.Corpus(root, tableFiles, TablesRagEval.Loads[1]).Where(d => d.FileName != TablesRagEval.RestrictedHiddenFile)], null, ct);
                restricted = system.ContainerOf(TablesRagEval.Dataset + "-restricted").ContainerId;
            }
        }

        Dictionary<string, McpToolbox> toolboxes = options.Arms.ToDictionary(a => a.Name,
            a => new McpToolbox(system.Services, McpToolbox.Reader(Guid.NewGuid(), "tables-arms-eval"), a.Tools, MemorySearchEval.Pinned));
        await using ToolBridge bridge = new();
        await bridge.StartAsync(ct);
        ClaudeCliAgent agent = new(bridge, b.Model, b.Effort, agentDir, rules: TablesRagEval.Rules);

        List<(TableQuestion Question, int Run, TableArm Arm)> schedule = Schedule(questions, options.Arms, options.Runs);
        ConcurrentDictionary<int, ArmRun> done = new();
        string? journalPath = b.OutPath is null ? null : Path.ChangeExtension(b.OutPath, ".runs.jsonl");
        Dictionary<(string Id, string Arm, int Run), string> saved = options.Resume && journalPath is not null && File.Exists(journalPath)
            ? SavedRuns(await File.ReadAllLinesAsync(journalPath, ct))
            : [];
        RunJournal journal = new(journalPath, keep: options.Resume);
        int finished = 0, resumed = 0;
        for (int i = 0; i < schedule.Count; i++)
            if (saved.TryGetValue((schedule[i].Question.Id, schedule[i].Arm.Name, schedule[i].Run), out string? line))
            {
                done[i] = ReadRun(line);
                resumed++;
            }
        if (options.Resume)
            log.WriteLine($"resuming: {resumed} of {schedule.Count} runs already saved");
        finished = resumed;
        await Parallel.ForEachAsync(Enumerable.Range(0, schedule.Count).Where(i => !done.ContainsKey(i)), new ParallelOptions { MaxDegreeOfParallelism = b.Workers, CancellationToken = ct }, async (i, token) =>
        {
            (TableQuestion q, int run, TableArm arm) = schedule[i];
            if (options.MinFreeMemoryBytes is long floor)
                await MemoryGate.WaitAsync(floor, MemoryGate.Available, TimeSpan.FromSeconds(30), log, token);
            string asked = q.Ask(seen);
            Stopwatch wall = Stopwatch.StartNew();
            AgentRun result = await agent.RunAsync(toolboxes[arm.Name], TablesRagEval.BasePrompt(q.Restricted ? restricted : container), asked, token);
            wall.Stop();
            TableGrade grade = TableGrade.Of(q, result);
            done[i] = new ArmRun(q, arm.Name, run, asked, result, grade, wall.Elapsed);
            await journal.AppendAsync(done[i], token);
            int n = Interlocked.Increment(ref finished);
            log.WriteLine($"[{n}/{schedule.Count}] {q.Id} {arm.Name} run {run + 1}: {result.Outcome} {(grade.Correct ? "right" : "wrong")}"
                + $"{(grade.Leaked ? " LEAK" : "")} calls {result.Calls.Count} {wall.Elapsed.TotalSeconds:F0}s"
                + $"{(result.Error is null ? "" : $" ({result.Error})")} | {agent.LastLimit}");
        });
        List<ArmRun> runs = [.. Enumerable.Range(0, schedule.Count).Select(i => done[i])];

        JsonObject report = Report(options, questions.Count, runs);
        if (seen.Count > 0)
            report["inventorySeen"] = new JsonObject
            {
                ["load1"] = TableQuestion.FormatSeen(seen[1]),
                ["load2"] = TableQuestion.FormatSeen(seen[2]),
            };
        if (options.Resume)
            report["resumedRuns"] = resumed;
        report["subscription"] = new JsonObject
        {
            ["atFirstRun"] = agent.FirstLimit?.ToString(),
            ["atLastRun"] = agent.LastLimit?.ToString(),
        };
        string json = report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (b.OutPath is not null)
        {
            await File.WriteAllTextAsync(b.OutPath, json + "\n", ct);
            // The journal holds every run in finishing order; rewrite it in schedule order now that all are in.
            await File.WriteAllLinesAsync(Path.ChangeExtension(b.OutPath, ".runs.jsonl"),
                schedule.Select((s, i) => saved.TryGetValue((s.Question.Id, s.Arm.Name, s.Run), out string? kept) ? kept : RunLines([runs[i]]).Single()), ct);
        }
        log.WriteLine(json);
        return 0;
    }

    /// <summary>
    /// Every run in the order it starts: question by question, each run of each arm. The arms swap order from one run to
    /// the next, so neither always goes first.
    /// </summary>
    public static List<(TableQuestion Question, int Run, TableArm Arm)> Schedule(IReadOnlyList<TableQuestion> questions, IReadOnlyList<TableArm> arms, int runs)
    {
        List<(TableQuestion, int, TableArm)> schedule = [];
        foreach (TableQuestion q in questions)
            for (int run = 0; run < runs; run++)
            {
                IEnumerable<TableArm> order = run % 2 == 0 ? arms : Enumerable.Reverse(arms);
                foreach (TableArm arm in order)
                    schedule.Add((q, run, arm));
            }
        return schedule;
    }

    /// <summary>One question's runs in one arm.</summary>
    public sealed record QuestionArm(TableQuestion Question, string Arm, IReadOnlyList<ArmRun> Runs)
    {
        /// <summary>Right in most of its runs: at least 2 of 3.</summary>
        public bool Correct => Runs.Count(r => r.Grade.Correct) * 2 > Runs.Count;

        /// <summary>The mean tool calls over the runs that got it right; null when none did.</summary>
        public double? CallsWhenRight => Runs.Any(r => r.Grade.Correct)
            ? Runs.Where(r => r.Grade.Correct).Average(r => (double)r.ToolCalls)
            : null;
    }

    public static List<QuestionArm> ByQuestion(IReadOnlyList<ArmRun> runs, string arm) =>
        [.. runs.Where(r => r.Arm == arm).GroupBy(r => r.Question.Id, StringComparer.Ordinal)
            .Select(g => new QuestionArm(g.First().Question, arm, [.. g.OrderBy(r => r.Run)]))];

    public static List<string> Nonconforming(Options options, int questions)
    {
        List<string> reasons = TablesRagEval.NonconformingTest(options.Base, questions, RegisteredTestOf(options.Set));
        if (options.Runs != RegisteredRuns)
            reasons.Add($"{options.Runs} runs per arm, not {RegisteredRuns}");
        if (!TableArm.All.All(options.Arms.Contains))
            reasons.Add("only arm " + string.Join(", ", options.Arms.Select(a => a.Name)) + " ran");
        return reasons;
    }

    public static JsonObject Report(Options options, int questions, IReadOnlyList<ArmRun> runs)
    {
        List<string> nonconforming = Nonconforming(options, questions);
        JsonObject arms = [];
        foreach (TableArm arm in options.Arms)
            arms[arm.Name] = ArmReport([.. runs.Where(r => r.Arm == arm.Name)], ByQuestion(runs, arm.Name));

        return new JsonObject
        {
            ["setup"] = "arms",
            ["set"] = options.Set,
            ["config"] = options.Base.Config,
            ["split"] = options.Base.Split,
            ["model"] = options.Base.Model,
            ["effort"] = options.Base.Effort,
            ["maxToolCalls"] = TablesRagEval.MaxToolCalls,
            ["runsPerArm"] = options.Runs,
            ["questions"] = questions,
            ["arms"] = arms,
            ["conforming"] = nonconforming.Count == 0,
            ["diagnosticOnly"] = new JsonArray([.. nonconforming.Select(r => (JsonNode)r)]),
        };
    }

    private static JsonObject ArmReport(IReadOnlyList<ArmRun> runs, IReadOnlyList<QuestionArm> questions)
    {
        JsonObject families = [];
        foreach (string family in questions.Select(q => q.Question.Family).Distinct().Order(StringComparer.Ordinal))
            families[family] = Accuracy([.. questions.Where(q => q.Question.Family == family)]);
        TokenUsage tokens = runs.Select(r => r.Agent.Usage).Aggregate(TokenUsage.Zero, (a, b) => a.Add(b));
        JsonObject outcomes = [];
        foreach (IGrouping<AgentOutcome, ArmRun> g in runs.GroupBy(r => r.Agent.Outcome).OrderBy(g => g.Key))
            outcomes[g.Key.ToString()] = g.Count();

        return new JsonObject
        {
            ["all"] = Accuracy(questions),
            ["families"] = families,
            ["admin"] = Accuracy([.. questions.Where(q => !q.Question.Restricted)]),
            ["restricted"] = Accuracy([.. questions.Where(q => q.Question.Restricted)]),
            ["runs"] = runs.Count,
            ["runAccuracy"] = Ratio(runs.Count(r => r.Grade.Correct), runs.Count),
            ["leakedRuns"] = runs.Count(r => r.Grade.Leaked),
            ["confidentWrongRuns"] = runs.Count(r => r.ConfidentWrong),
            ["toolCalls"] = Spread([.. runs.Select(r => (double)r.ToolCalls)]),
            ["searchCalls"] = Spread([.. runs.Select(r => (double)r.SearchCalls)]),
            ["wallSeconds"] = Spread([.. runs.Select(r => r.Wall.TotalSeconds)]),
            ["outcomes"] = outcomes,
            ["tokens"] = new JsonObject
            {
                ["input"] = tokens.Input,
                ["cacheWrite"] = tokens.CacheWrite,
                ["cacheRead"] = tokens.CacheRead,
                ["output"] = tokens.Output,
                ["apiListPriceUsd"] = Math.Round(tokens.CostUsd, 4),
            },
            ["costPerQuestionUsd"] = Math.Round(CostPerQuestion(runs), 4),
        };
    }

    /// <summary>The average API list-price cost of one run, which is one question asked once.</summary>
    public static double CostPerQuestion(IReadOnlyList<ArmRun> runs) => runs.Count == 0 ? 0 : runs.Average(r => r.Agent.Usage.CostUsd);

    private static JsonObject Accuracy(IReadOnlyList<QuestionArm> questions) => new()
    {
        ["questions"] = questions.Count,
        ["correct"] = questions.Count(q => q.Correct),
        ["accuracy"] = Ratio(questions.Count(q => q.Correct), questions.Count),
    };

    private static double Ratio(int count, int of) => of == 0 ? 0 : Math.Round((double)count / of, 4);

    /// <summary>Mean, median and 90th percentile, the percentiles as NumPy's default (linear) gives them.</summary>
    public static JsonObject Spread(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return new JsonObject { ["mean"] = 0, ["median"] = 0, ["p90"] = 0 };
        double[] sorted = [.. values.Order()];
        return new JsonObject
        {
            ["mean"] = Math.Round(sorted.Average(), 2),
            ["median"] = Math.Round(SortedArrayStatistics.QuantileCustom(sorted, 0.5, QuantileDefinition.R7), 2),
            ["p90"] = Math.Round(SortedArrayStatistics.QuantileCustom(sorted, 0.9, QuantileDefinition.R7), 2),
        };
    }

    public static IEnumerable<string> RunLines(IEnumerable<ArmRun> runs)
    {
        foreach (ArmRun r in runs)
        {
            JsonObject line = TablesRagEval.RunLine(new TablesRagEval.QuestionResult(r.Question, r.Asked, r.Agent, r.Grade));
            line["arm"] = r.Arm;
            line["run"] = r.Run + 1;
            line["toolCalls"] = r.ToolCalls;
            line["searchCalls"] = r.SearchCalls;
            line["confidentWrong"] = r.ConfidentWrong;
            line["wallSeconds"] = Math.Round(r.Wall.TotalSeconds, 2);
            line["tokens"] = new JsonObject
            {
                ["input"] = r.Agent.Usage.Input,
                ["cacheWrite"] = r.Agent.Usage.CacheWrite,
                ["cacheRead"] = r.Agent.Usage.CacheRead,
                ["output"] = r.Agent.Usage.Output,
            };
            yield return line.ToJsonString();
        }
    }

    /// <summary>
    /// One <see cref="RunLines"/> line read back, with the grade it was given when it ran. The question carries only what
    /// scoring reads (id, family, user, expected answer), and each call's output is the logged, possibly cut, text.
    /// </summary>
    public static ArmRun ReadRun(string line)
    {
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement r = doc.RootElement;
        TableQuestion question = new(r.GetProperty("id").GetString()!, "", r.GetProperty("family").GetString()!, "",
            r.GetProperty("question").GetString()!, r.GetProperty("user").GetString()!, "", r.GetProperty("expected").Clone(),
            null, null, [], []);
        List<ToolTrace> calls = [.. r.GetProperty("calls").EnumerateArray().Select(c => new ToolTrace(
            c.GetProperty("tool").GetString()!,
            c.GetProperty("input").ValueKind == JsonValueKind.Object
                ? c.GetProperty("input").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                : new Dictionary<string, JsonElement>(),
            c.GetProperty("output").GetString() ?? ""))];
        JsonElement tokens = r.GetProperty("tokens");
        TokenUsage usage = new(tokens.GetProperty("input").GetInt64(), tokens.GetProperty("output").GetInt64(),
            tokens.GetProperty("cacheRead").GetInt64(), tokens.GetProperty("cacheWrite").GetInt64(), r.GetProperty("costUsd").GetDouble());
        JsonElement? answer = r.TryGetProperty("answer", out JsonElement a) && a.ValueKind != JsonValueKind.Null ? a.Clone() : null;
        string? error = r.TryGetProperty("error", out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        AgentRun agent = new(Enum.Parse<AgentOutcome>(r.GetProperty("outcome").GetString()!), answer?.ToString(), [], calls, usage,
            r.GetProperty("turns").GetInt32(), error, answer);
        return new ArmRun(question, r.GetProperty("arm").GetString()!, r.GetProperty("run").GetInt32() - 1, question.Question, agent,
            new TableGrade(r.GetProperty("correct").GetBoolean(), r.GetProperty("leaked").GetBoolean()),
            TimeSpan.FromSeconds(r.GetProperty("wallSeconds").GetDouble()));
    }

    /// <summary>
    /// Scores a finished run's <c>.runs.jsonl</c> without asking anything again, for a run whose report step failed. The
    /// options are the ones the run was started with; each question counts once however many lines it has.
    /// </summary>
    public static async Task<int> ScoreAsync(Options options, string runsPath, TextWriter log, CancellationToken ct)
    {
        List<ArmRun> runs = [.. (await File.ReadAllLinesAsync(runsPath, ct)).Where(l => l.Length > 0).Select(ReadRun)];
        int questions = runs.Select(r => r.Question.Id).Distinct(StringComparer.Ordinal).Count();
        JsonObject report = Report(options, questions, runs);
        report["scoredFrom"] = Path.GetFileName(runsPath);
        string json = report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (options.Base.OutPath is not null)
            await File.WriteAllTextAsync(options.Base.OutPath, json + "\n", ct);
        log.WriteLine(json);
        return 0;
    }
}

/// <summary>
/// Writes each run's line to <c>&lt;out&gt;.runs.jsonl</c> the moment it finishes, so a run that is cut short keeps
/// what it measured. Lines land in finishing order; the file is emptied when the journal is created.
/// </summary>
public sealed class RunJournal
{
    private readonly string? _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="keep">Append after the lines already in the file, for a resumed run, instead of emptying it.</param>
    public RunJournal(string? path, bool keep = false)
    {
        _path = path;
        if (path is not null && !(keep && File.Exists(path)))
            File.WriteAllText(path, "");
    }

    public async Task AppendAsync(ArmRun run, CancellationToken ct)
    {
        if (_path is null)
            return;
        string line = TablesArmsEval.RunLines([run]).Single() + "\n";
        await _gate.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(_path, line, ct);
        }
        finally
        {
            _gate.Release();
        }
    }
}
