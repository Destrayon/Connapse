using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Eval.Cli;
using Connapse.Eval.Datasets;
using Connapse.Eval.Model;
using Connapse.Eval.Runs.MemorySearch;
using Connapse.Eval.Statistics;
using Connapse.Eval.Systems;
using MathNet.Numerics.Statistics;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Eval.Runs;

/// <summary>One way for an agent to answer table questions: the MCP tools it gets.</summary>
public sealed record TableArm(string Name, IReadOnlyList<string> Tools)
{
    public const string SqlTool = "table_sql";

    /// <summary>Arm S: the four structured table tools, plus search.</summary>
    public static readonly TableArm Structured = new("structured",
        ["table_list", "table_describe", "table_read", "table_aggregate", .. MemorySearchEval.SearchTools]);

    /// <summary>Arm Q: the agent writes its own SQL, with table listing, description and search to find its way.</summary>
    public static readonly TableArm Sql = new("sql", [SqlTool, "table_list", "table_describe", .. MemorySearchEval.SearchTools]);

    public static readonly IReadOnlyList<TableArm> All = [Structured, Sql];

    /// <summary>The arms named on the command line, in the order given; both when none are named.</summary>
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
    /// <summary>Every tool call before the answer, refused SQL and tool errors included.</summary>
    public int ToolCalls => Agent.Calls.Count;

    public int SearchCalls => Agent.Calls.Count(c => MemorySearchEval.SearchTools.Contains(c.Name));

    /// <summary>An answer given and graded wrong; running out of calls or giving no answer is not confident.</summary>
    public bool ConfidentWrong => Agent.Outcome == AgentOutcome.Answered && !Grade.Correct;
}

/// <summary>
/// CON-327: arm S (structured table tools) and arm Q (<c>table_sql</c>) on the same tables-v1 questions, so the CON-320
/// head-to-head can be scored by its pass bar (<c>docs/plans/con-320-raw-sql-rls.md</c>). The data, agent, prompt and
/// grading are the rag setup's (<see cref="TablesRagEval"/>); only the tools differ. Each question gets
/// <see cref="Options.Runs"/> runs per arm, interleaved, and counts as right for an arm when most of its runs are right.
/// </summary>
public sealed class TablesArmsEval(RepoPaths paths, HttpClient http, TextWriter log)
{
    public const int RegisteredRuns = 3;

    /// <summary>The pass bar's margins: a 5-point accuracy gap, p &lt; 0.05, and arm Q at most 1.2× arm S's cost.</summary>
    public const double WinPoints = 5;

    public const double Alpha = 0.05;

    public const double MaxCostRatio = 1.2;

    /// <summary>The family gap the arm that becomes the default may not exceed.</summary>
    public const double MaxFamilyGapPoints = 10;

    /// <param name="Base">The data, split, model and slice, as the rag setup takes them; its mixed questions are not asked.</param>
    public sealed record Options(TablesRagEval.Options Base, IReadOnlyList<TableArm> Arms, int Runs);

    public async Task<int> RunAsync(Options options, CancellationToken ct)
    {
        foreach (TableArm arm in options.Arms)
            if (arm.Missing() is { Count: > 0 } missing)
            {
                log.WriteLine($"Arm {arm.Name} needs {string.Join(", ", missing)}, which this build's MCP server doesn't have"
                    + (missing.Contains(TableArm.SqlTool) ? " (table_sql arrives with CON-326)." : "."));
                return 2;
            }

        TablesRagEval.Options b = options.Base;
        string agentDir = Path.GetFullPath(b.AgentDir ?? Path.Combine(Path.GetTempPath(), "connapse-tables-arms-agent"));
        Directory.CreateDirectory(agentDir);
        MemorySearchEval.RequireNoProjectMemory(agentDir);

        DatasetEntry entry = EvalManifest.Load(paths.ManifestPath).Datasets[TablesRagEval.Dataset];
        DatasetCache cache = new(paths.CacheRoot, http, paths.DatasetsRoot);
        await cache.EnsureAsync(TablesRagEval.Dataset, entry, allowUnpinned: false, ct);
        string root = cache.DirectoryFor(TablesRagEval.Dataset, entry);

        List<TableQuestion> questions = TablesRagEval.Select(TableQuestion.ReadAll(Path.Combine(root, "questions.jsonl"))
            .Where(q => q.Split == b.Split), b);
        List<string> tableFiles = [.. entry.Files.Select(f => f.Name).Where(n => n.EndsWith(".csv", StringComparison.Ordinal)
            && !n.StartsWith("linked-", StringComparison.Ordinal) && TablesRagEval.InSlice(b.Tables, n))];

        SystemConfig config = SystemConfig.Load(paths.EvalRoot, "connapse", b.Config);
        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        await using ConnapseSearchSystem system = await ConnapseSearchSystem.StartAsync(config, paths.WebContentRoot, embeddings, log, null, ct);
        if (b.StallMinutes is int stallMinutes)
            system.StallTimeout = TimeSpan.FromMinutes(stallMinutes);

        // The same containers the rag setup builds: load 1, then load 2 over it, then the restricted user's view.
        TablesRagEval setup = new(paths, http, log);
        Dictionary<int, DateTime> seen = [];
        await setup.IndexAsync(system, TablesRagEval.Dataset, TablesRagEval.Corpus(root, tableFiles, TablesRagEval.Loads[0]), null, ct);
        (Guid container, _) = system.ContainerOf(TablesRagEval.Dataset);
        seen[1] = DateTime.UtcNow;
        await setup.IndexAsync(system, TablesRagEval.Dataset + "-load2", [TablesRagEval.TableDocument(root, TablesRagEval.Loads[1])], container, ct);
        seen[2] = DateTime.UtcNow;
        log.WriteLine($"inventory load 1 seen {TableQuestion.FormatSeen(seen[1])}, load 2 seen {TableQuestion.FormatSeen(seen[2])}");

        Guid restricted = Guid.Empty;
        if (questions.Any(q => q.Restricted))
        {
            await setup.IndexAsync(system, TablesRagEval.Dataset + "-restricted",
                [.. TablesRagEval.Corpus(root, tableFiles, TablesRagEval.Loads[1]).Where(d => d.FileName != TablesRagEval.RestrictedHiddenFile)], null, ct);
            restricted = system.ContainerOf(TablesRagEval.Dataset + "-restricted").ContainerId;
        }

        if (options.Arms.Contains(TableArm.Sql))
        {
            await using AsyncServiceScope scope = system.Services.CreateAsyncScope();
            IContainerStore store = scope.ServiceProvider.GetRequiredService<IContainerStore>();
            foreach (Guid id in new[] { container, restricted }.Where(id => id != Guid.Empty))
                await TurnOnAgentSqlAsync(store, id, ct);
        }

        Dictionary<string, McpToolbox> toolboxes = options.Arms.ToDictionary(a => a.Name,
            a => new McpToolbox(system.Services, McpToolbox.Reader(Guid.NewGuid(), "tables-arms-eval"), a.Tools, MemorySearchEval.Pinned));
        await using ToolBridge bridge = new();
        await bridge.StartAsync(ct);
        ClaudeCliAgent agent = new(bridge, b.Model, b.Effort, agentDir, rules: TablesRagEval.Rules);

        List<(TableQuestion Question, int Run, TableArm Arm)> schedule = Schedule(questions, options.Arms, options.Runs);
        ConcurrentDictionary<int, ArmRun> done = new();
        RunJournal journal = new(b.OutPath is null ? null : Path.ChangeExtension(b.OutPath, ".runs.jsonl"));
        int finished = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, schedule.Count), new ParallelOptions { MaxDegreeOfParallelism = b.Workers, CancellationToken = ct }, async (i, token) =>
        {
            (TableQuestion q, int run, TableArm arm) = schedule[i];
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
        report["inventorySeen"] = new JsonObject
        {
            ["load1"] = TableQuestion.FormatSeen(seen[1]),
            ["load2"] = TableQuestion.FormatSeen(seen[2]),
        };
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
            await File.WriteAllLinesAsync(Path.ChangeExtension(b.OutPath, ".runs.jsonl"), RunLines(runs), ct);
        }
        log.WriteLine(json);
        return 0;
    }

    private static readonly JsonSerializerOptions OverridesJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Turns on the container's <c>Tables.AgentSql</c> setting, which <c>table_sql</c> refuses without. Set through the
    /// stored overrides' JSON so this compiles before the setting exists; a build without it fails here, not mid-run.
    /// </summary>
    private static async Task TurnOnAgentSqlAsync(IContainerStore store, Guid container, CancellationToken ct)
    {
        JsonObject overrides = WithAgentSql(await store.GetSettingsOverridesJsonAsync(container, ct));
        await store.SaveSettingsOverridesAsync(container, overrides.Deserialize<ContainerSettingsOverrides>(OverridesJson)!, ct);
        if (!AgentSqlIsOn(await store.GetSettingsOverridesJsonAsync(container, ct)))
            throw new InvalidOperationException($"Container {container} didn't keep Tables.AgentSql; this build has no such setting.");
    }

    /// <summary>The stored overrides with <c>tables.agentSql</c> set to true and everything else kept.</summary>
    public static JsonObject WithAgentSql(JsonElement? stored)
    {
        JsonObject root = stored is { ValueKind: JsonValueKind.Object } s ? JsonNode.Parse(s.GetRawText())!.AsObject() : [];
        if (root["tables"] is not JsonObject tables)
            root["tables"] = tables = [];
        tables["agentSql"] = true;
        return root;
    }

    public static bool AgentSqlIsOn(JsonElement? stored) =>
        stored is { ValueKind: JsonValueKind.Object } s
        && s.TryGetProperty("tables", out JsonElement tables) && tables.ValueKind == JsonValueKind.Object
        && tables.TryGetProperty("agentSql", out JsonElement on) && on.ValueKind == JsonValueKind.True;

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
        List<string> reasons = TablesRagEval.NonconformingTest(options.Base, questions);
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
            ["config"] = options.Base.Config,
            ["split"] = options.Base.Split,
            ["model"] = options.Base.Model,
            ["effort"] = options.Base.Effort,
            ["maxToolCalls"] = TablesRagEval.MaxToolCalls,
            ["runsPerArm"] = options.Runs,
            ["questions"] = questions,
            ["arms"] = arms,
            ["comparison"] = options.Arms.Contains(TableArm.Structured) && options.Arms.Contains(TableArm.Sql)
                ? Compare(ByQuestion(runs, TableArm.Structured.Name), ByQuestion(runs, TableArm.Sql.Name),
                    [.. runs.Where(r => r.Arm == TableArm.Structured.Name)], [.. runs.Where(r => r.Arm == TableArm.Sql.Name)])
                : null,
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

    /// <summary>
    /// The pass bar's comparisons (items 2–4): exact McNemar on each question's majority right-or-wrong, the family gaps,
    /// paired Wilcoxon on tool calls where both arms got the question right, and arm Q's cost against arm S's.
    /// </summary>
    public static JsonObject Compare(IReadOnlyList<QuestionArm> structured, IReadOnlyList<QuestionArm> sql,
        IReadOnlyList<ArmRun> structuredRuns, IReadOnlyList<ArmRun> sqlRuns)
    {
        Dictionary<string, QuestionArm> s = structured.ToDictionary(q => q.Question.Id, StringComparer.Ordinal);
        List<(QuestionArm S, QuestionArm Q)> pairs = [.. sql.Where(q => s.ContainsKey(q.Question.Id)).Select(q => (s[q.Question.Id], q))];

        int onlyStructured = pairs.Count(p => p.S.Correct && !p.Q.Correct);
        int onlySql = pairs.Count(p => !p.S.Correct && p.Q.Correct);
        double points = pairs.Count == 0 ? 0 : 100.0 * (onlySql - onlyStructured) / pairs.Count;
        double mcnemar = PairedTests.McNemarExact(onlyStructured, onlySql);

        JsonObject familyGaps = [];
        foreach (string family in pairs.Select(p => p.S.Question.Family).Distinct().Order(StringComparer.Ordinal))
        {
            List<(QuestionArm S, QuestionArm Q)> f = [.. pairs.Where(p => p.S.Question.Family == family)];
            familyGaps[family] = Math.Round(100.0 * (f.Count(p => p.Q.Correct) - f.Count(p => p.S.Correct)) / f.Count, 2);
        }

        List<(double S, double Q)> calls = [.. pairs.Where(p => p.S.Correct && p.Q.Correct && p.S.CallsWhenRight is not null && p.Q.CallsWhenRight is not null)
            .Select(p => (p.S.CallsWhenRight!.Value, p.Q.CallsWhenRight!.Value))];
        double wilcoxon = PairedTests.WilcoxonPValue([.. calls.Select(c => c.Q - c.S)]);
        double medianS = Median([.. calls.Select(c => c.S)]);
        double medianQ = Median([.. calls.Select(c => c.Q)]);

        double costS = CostPerQuestion(structuredRuns);
        double costQ = CostPerQuestion(sqlRuns);
        double? costRatio = costS > 0 ? costQ / costS : null;

        return new JsonObject
        {
            ["pairs"] = pairs.Count,
            ["correctAnswers"] = new JsonObject
            {
                ["onlyStructured"] = onlyStructured,
                ["onlySql"] = onlySql,
                ["sqlMinusStructuredPoints"] = Math.Round(points, 2),
                ["mcnemarExactP"] = mcnemar,
                ["verdict"] = CorrectVerdict(points, mcnemar),
                ["familyGapPoints"] = familyGaps,
            },
            ["toolCallsWhenBothRight"] = new JsonObject
            {
                ["questions"] = calls.Count,
                ["structuredMedian"] = Math.Round(medianS, 2),
                ["sqlMedian"] = Math.Round(medianQ, 2),
                ["wilcoxonP"] = double.IsNaN(wilcoxon) ? null : wilcoxon,
                ["verdict"] = CallsVerdict(medianS, medianQ, wilcoxon),
            },
            ["cost"] = new JsonObject
            {
                ["structuredPerQuestionUsd"] = Math.Round(costS, 4),
                ["sqlPerQuestionUsd"] = Math.Round(costQ, 4),
                ["sqlOverStructured"] = costRatio is double r ? Math.Round(r, 3) : null,
                ["withinBar"] = costRatio is double ratio ? ratio <= MaxCostRatio : null,
            },
        };
    }

    private static double Median(IReadOnlyList<double> values) =>
        values.Count == 0 ? 0 : SortedArrayStatistics.QuantileCustom([.. values.Order()], 0.5, QuantileDefinition.R7);

    /// <summary>Pass bar item 2: arm Q wins or loses at a 5-point gap with p &lt; 0.05; anything else ties.</summary>
    public static string CorrectVerdict(double sqlMinusStructuredPoints, double p) =>
        p >= Alpha ? "tie"
        : sqlMinusStructuredPoints >= WinPoints ? "sql wins"
        : sqlMinusStructuredPoints <= -WinPoints ? "sql loses"
        : "tie";

    /// <summary>Pass bar item 3: the lower median wins at p &lt; 0.05.</summary>
    public static string CallsVerdict(double structuredMedian, double sqlMedian, double p) =>
        double.IsNaN(p) || p >= Alpha || structuredMedian == sqlMedian ? "tie"
        : sqlMedian < structuredMedian ? "sql wins" : "structured wins";

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
}

/// <summary>
/// Writes each run's line to <c>&lt;out&gt;.runs.jsonl</c> the moment it finishes, so a run that is cut short keeps
/// what it measured. Lines land in finishing order; the file is emptied when the journal is created.
/// </summary>
public sealed class RunJournal
{
    private readonly string? _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RunJournal(string? path)
    {
        _path = path;
        if (path is not null)
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
