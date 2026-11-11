using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Eval.Cli;
using Connapse.Eval.Datasets;
using Connapse.Eval.Metrics;
using Connapse.Eval.Model;
using Connapse.Eval.Runs.MemorySearch;
using Connapse.Eval.Systems;

namespace Connapse.Eval.Runs;

/// <summary>One tables-v1 or mixed question, as <c>eval/datasets/tables</c> writes it.</summary>
public sealed record TableQuestion(
    string Id, string Split, string Family, string Template, string Question, string User, string AnswerType,
    JsonElement Answer, JsonElement? AdminAnswer, int? AsOfLoad, IReadOnlyList<string> Tables, IReadOnlyList<string> Documents)
{
    public const string AsOfPlaceholder = "{as_of}";

    public bool Restricted => User == "restricted";

    public static List<TableQuestion> ReadAll(string path) =>
        [.. File.ReadLines(path).Where(l => l.Length > 0).Select(Parse)];

    public static TableQuestion Parse(string line)
    {
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement q = doc.RootElement;
        JsonElement? Optional(string name) =>
            q.TryGetProperty(name, out JsonElement e) && e.ValueKind != JsonValueKind.Null ? e.Clone() : null;
        List<string> Strings(string name) =>
            q.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Array ? [.. e.EnumerateArray().Select(s => s.GetString()!)] : [];
        return new TableQuestion(
            q.GetProperty("id").GetString()!, q.GetProperty("split").GetString()!, q.GetProperty("family").GetString()!,
            q.GetProperty("template").GetString()!, q.GetProperty("question").GetString()!, q.GetProperty("user").GetString()!,
            q.GetProperty("answer_type").GetString()!, q.GetProperty("answer").Clone(), Optional("admin_answer"),
            Optional("as_of_load")?.GetInt32(), Strings("tables"), Strings("documents"));
    }

    /// <summary>
    /// The question as the agent sees it: a seen-as-of question names the time its inventory load settled in Connapse.
    /// Throws when a question and its load disagree, so a broken set can't be asked half-filled.
    /// </summary>
    public string Ask(IReadOnlyDictionary<int, DateTime> loadsSeenAt)
    {
        bool hasPlaceholder = Question.Contains(AsOfPlaceholder, StringComparison.Ordinal);
        if (AsOfLoad is not int load)
            return hasPlaceholder ? throw new InvalidDataException($"{Id} has {AsOfPlaceholder} but no as_of_load.") : Question;
        if (!hasPlaceholder)
            throw new InvalidDataException($"{Id} has as_of_load {load} but no {AsOfPlaceholder}.");
        if (!loadsSeenAt.TryGetValue(load, out DateTime seen))
            throw new InvalidDataException($"{Id} asks about inventory load {load}, which was never loaded.");
        return Question.Replace(AsOfPlaceholder, FormatSeen(seen), StringComparison.Ordinal);
    }

    public static string FormatSeen(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
}

/// <summary>A graded answer. A restricted question's answer that matches the admin answer is a leak.</summary>
public sealed record TableGrade(bool Correct, bool Leaked)
{
    public static TableGrade Of(TableQuestion question, AgentRun run)
    {
        if (run.AnswerValue is not { } value)
            return new TableGrade(false, false);
        bool correct = TableAnswerGrader.Grade(value, question.Answer, question.AnswerType);
        bool leaked = question.Restricted && question.AdminAnswer is { } admin
            && TableAnswerGrader.Grade(value, admin, question.AnswerType);
        return new TableGrade(correct, leaked);
    }
}

/// <summary>
/// CON-E6's rag setup (CON-299): the tables-v1 CSV files in a documents container, parsed as text by TextParser and
/// searched with the MCP server's own <c>search_knowledge</c> and <c>get_document</c>, answered by an agent through the
/// CON-244 harness. Its score is the baseline the tables container must beat. The rules are in <c>eval/pass-bars.md</c>
/// (Tables container), written before any scored run.
/// </summary>
public sealed class TablesRagEval(RepoPaths paths, HttpClient http, TextWriter log)
{
    public const string Dataset = "tables";

    /// <summary>The pass bar's "at most 25 turns", counted as tool calls the way the harness counts them.</summary>
    public const int MaxToolCalls = 25;

    public static readonly AgentRules Rules = new(MaxToolCalls, AgentContract.TypedAnswer, TypedAnswer: true);

    public const int RegisteredTest = 180;
    public const int RegisteredMixed = 30;

    /// <summary>The current inventory goes in under one path, so load 2 replaces load 1 as a re-upload does.</summary>
    public const string InventoryFile = "inventory.csv";

    public const string RestrictedHiddenFile = "tickets-security.csv";

    public static readonly string[] Loads = ["inventory.load1.csv", "inventory.load2.csv"];

    public sealed record Options(
        string Config, string Split, string Model, string Effort, int? LimitTasks, IReadOnlyList<string> Ids, bool SkipMixed,
        int Workers, string? OutPath, string? AgentDir = null, IReadOnlyList<string>? Tables = null, int? StallMinutes = null,
        string Setup = RagSetup);

    public const string RagSetup = "rag";
    public const string TablesSetup = "tables";
    public const string BothSetup = "both";

    /// <summary>The pass bar's "four table tools" (CON-305). table_changes came later (CON-306) and is not one of them.</summary>
    public static readonly string[] TableTools = ["table_list", "table_describe", "table_read", "table_aggregate"];

    /// <summary>
    /// The tools a setup offers on the test questions: rag the two search tools, tables the four table tools, both all six.
    /// Anything else throws, so a typo can't run a setup the bar never named.
    /// </summary>
    public static string[] ToolsOf(string setup) => setup switch
    {
        RagSetup => MemorySearchEval.SearchTools,
        TablesSetup => TableTools,
        BothSetup => [.. MemorySearchEval.SearchTools, .. TableTools],
        _ => throw new ArgumentException($"Unknown setup '{setup}': use {RagSetup}, {TablesSetup} or {BothSetup}.", nameof(setup)),
    };

    /// <summary>
    /// The setup the mixed set is asked with. The bar compares "both" with rag there: a mixed question needs a policy
    /// document, which the table tools alone can't read, so a tables run asks it with both.
    /// </summary>
    public static string MixedSetupOf(string setup) => setup == RagSetup ? RagSetup : BothSetup;

    /// <summary>The table a file holds: <c>orders.csv</c> is <c>orders</c>, both inventory loads are <c>inventory</c>.</summary>
    public static string TableOf(string file) =>
        Loads.Contains(file) ? "inventory" : file.EndsWith(".csv", StringComparison.Ordinal) ? file[..^4] : file;

    /// <summary>
    /// A slice for a quick check: only the named tables are indexed, and without <c>--ids</c> only the questions that
    /// need nothing else are asked. Inventory always loads, since the seen-as-of times come from it. Tickets questions
    /// name the table <c>tickets</c>, which every tickets-* file belongs to.
    /// </summary>
    public static bool InSlice(IReadOnlyList<string>? tables, string file) =>
        tables is not { Count: > 0 } || Loads.Contains(file)
        || tables.Any(t => TableOf(file) == t || TableOf(file).StartsWith(t + "-", StringComparison.Ordinal));

    public sealed record QuestionResult(TableQuestion Question, string Asked, AgentRun Run, TableGrade Grade);

    /// <summary>
    /// The base prompt every CON-E6 setup shares; only the container and the tools differ. It never mentions how the
    /// data is stored, so it reads the same for documents and tables.
    /// </summary>
    public static string BasePrompt(Guid container) =>
        $"You answer questions about a company's data, stored in the Connapse container with ID {container}. " +
        "Use the tools to find the data the question needs and work the answer out from it. When you are sure, call " +
        "the answer tool once with the answer as a typed JSON value: a number without units or thousands separators, " +
        "a string, true or false, or a list of numbers or strings when the question asks for several. " +
        $"You may make at most {MaxToolCalls} tool calls before answering.";

    public async Task<int> RunAsync(Options options, CancellationToken ct)
    {
        string[] testTools = ToolsOf(options.Setup);
        string[] mixedTools = ToolsOf(MixedSetupOf(options.Setup));
        string agentDir = Path.GetFullPath(options.AgentDir ?? Path.Combine(Path.GetTempPath(), $"connapse-tables-{options.Setup}-agent"));
        Directory.CreateDirectory(agentDir);
        MemorySearchEval.RequireNoProjectMemory(agentDir);

        DatasetEntry entry = EvalManifest.Load(paths.ManifestPath).Datasets[Dataset];
        DatasetCache cache = new(paths.CacheRoot, http, paths.DatasetsRoot);
        await cache.EnsureAsync(Dataset, entry, allowUnpinned: false, ct);
        string root = cache.DirectoryFor(Dataset, entry);

        List<TableQuestion> questions = Select(TableQuestion.ReadAll(Path.Combine(root, "questions.jsonl"))
            .Where(q => q.Split == options.Split), options);
        List<TableQuestion> mixed = options.SkipMixed ? [] : Select(TableQuestion.ReadAll(Path.Combine(root, "mixed.jsonl")), options);
        List<string> tableFiles = [.. entry.Files.Select(f => f.Name).Where(n => n.EndsWith(".csv", StringComparison.Ordinal)
            && !n.StartsWith("linked-", StringComparison.Ordinal) && InSlice(options.Tables, n))];

        SystemConfig config = SystemConfig.Load(paths.EvalRoot, "connapse", options.Config);
        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        await using ConnapseSearchSystem system = await ConnapseSearchSystem.StartAsync(config, paths.WebContentRoot, embeddings, log, null, ct);
        if (options.StallMinutes is int stallMinutes)
            system.StallTimeout = TimeSpan.FromMinutes(stallMinutes);

        // Load 1 with every other table, then load 2 over it at the same path; each load is seen when it settles.
        Dictionary<int, DateTime> seen = [];
        await IndexAsync(system, Dataset, Corpus(root, tableFiles, Loads[0]), null, ct);
        (Guid container, _) = system.ContainerOf(Dataset);
        seen[1] = DateTime.UtcNow;
        await IndexAsync(system, Dataset + "-load2", [TableDocument(root, Loads[1])], container, ct);
        seen[2] = DateTime.UtcNow;
        log.WriteLine($"inventory load 1 seen {TableQuestion.FormatSeen(seen[1])}, load 2 seen {TableQuestion.FormatSeen(seen[2])}");

        // The restricted user's view: the same tables as they stand now, without the security queue's tickets.
        Guid restricted = Guid.Empty;
        if (questions.Any(q => q.Restricted))
        {
            await IndexAsync(system, Dataset + "-restricted",
                [.. Corpus(root, tableFiles, Loads[1]).Where(d => d.FileName != RestrictedHiddenFile)], null, ct);
            restricted = system.ContainerOf(Dataset + "-restricted").ContainerId;
        }

        Guid reader = Guid.NewGuid();
        McpToolbox tools = new(system.Services, McpToolbox.Reader(reader, $"tables-{options.Setup}-eval"), testTools, MemorySearchEval.Pinned);
        McpToolbox bothTools = new(system.Services, McpToolbox.Reader(reader, $"tables-{options.Setup}-eval"), mixedTools, MemorySearchEval.Pinned);
        await using ToolBridge bridge = new();
        await bridge.StartAsync(ct);
        ClaudeCliAgent agent = new(bridge, options.Model, options.Effort, agentDir, rules: Rules);

        List<QuestionResult> results = await AnswerAsync(agent, tools,
            q => BasePrompt(q.Restricted ? restricted : container), questions, seen, options.Workers, ct);

        List<QuestionResult> mixedResults = [];
        if (mixed.Count > 0)
        {
            // The policies arrive only now, so the table questions above were asked of the tables alone.
            List<EvalDocument> policies = [.. entry.Files.Select(f => f.Name)
                .Where(n => n.StartsWith("policy-", StringComparison.Ordinal))
                .Select(n => new EvalDocument(n, DocumentKind.Text, n["policy-".Length..], File.ReadAllText(Path.Combine(root, n)), null,
                    new Dictionary<string, string>()) { FileName = n["policy-".Length..], Folder = "/docs" })];
            await IndexAsync(system, Dataset + "-policies", policies, container, ct);
            mixedResults = await AnswerAsync(agent, bothTools, _ => BasePrompt(container), mixed, seen, options.Workers, ct);
        }

        JsonObject report = Report(options, results, mixedResults);
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
        if (options.OutPath is not null)
        {
            await File.WriteAllTextAsync(options.OutPath, json + "\n", ct);
            await File.WriteAllLinesAsync(Path.ChangeExtension(options.OutPath, ".runs.jsonl"), RunLines([.. results, .. mixedResults]), ct);
        }
        log.WriteLine(json);
        return 0;
    }

    internal static List<TableQuestion> Select(IEnumerable<TableQuestion> questions, Options options)
    {
        if (options.Ids.Count > 0)
            questions = questions.Where(q => options.Ids.Contains(q.Id));
        else if (options.Tables is { Count: > 0 } tables)
            questions = questions.Where(q => q.Tables.All(t => t == "inventory" || tables.Contains(t)) && q.Documents.Count == 0);
        return options.LimitTasks is int limit ? [.. questions.Take(limit)] : [.. questions];
    }

    internal async Task IndexAsync(ConnapseSearchSystem system, string name, List<EvalDocument> corpus, Guid? into, CancellationToken ct)
    {
        IndexReport report = await system.IndexAsync(new EvalDataset(name, "v1", [], corpus, [], new Qrels()), IngestionWait.ThrowOnStall, ct, into);
        MemorySearchEval.RequireCompleteIndex(report, corpus.Count, system.ContainerOf(name).DocMap.Count);
        foreach (DocumentOutcome o in report.Outcomes)
            log.WriteLine($"[{name}] {o.DatasetDocId} ready after {o.Elapsed.TotalMinutes:F1} min");
        log.WriteLine($"[{name}] {corpus.Count} files indexed");
    }

    /// <summary>Every table file in the root folder under its own name, with <paramref name="inventoryLoad"/> as the inventory.</summary>
    public static List<EvalDocument> Corpus(string root, IEnumerable<string> tableFiles, string inventoryLoad) =>
        [.. tableFiles.Where(n => !Loads.Contains(n)).Select(n => TableDocument(root, n)), TableDocument(root, inventoryLoad)];

    /// <summary>A CSV uploaded as the user's file would be: its name picks TextParser, and its text is not touched.</summary>
    public static EvalDocument TableDocument(string root, string file)
    {
        string name = Loads.Contains(file) ? InventoryFile : file;
        string text = File.ReadAllText(Path.Combine(root, file));
        return new EvalDocument(file, DocumentKind.Text, name, text, null, new Dictionary<string, string>())
        {
            FileName = name,
            Folder = "/",
        };
    }

    private async Task<List<QuestionResult>> AnswerAsync(ClaudeCliAgent agent, McpToolbox tools, Func<TableQuestion, string> prompt,
        IReadOnlyList<TableQuestion> questions, IReadOnlyDictionary<int, DateTime> seen, int workers, CancellationToken ct)
    {
        ConcurrentDictionary<string, QuestionResult> done = new(StringComparer.Ordinal);
        int finished = 0;
        await Parallel.ForEachAsync(questions, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct }, async (q, token) =>
        {
            string asked = q.Ask(seen);
            AgentRun run = await agent.RunAsync(tools, prompt(q), asked, token);
            TableGrade grade = TableGrade.Of(q, run);
            done[q.Id] = new QuestionResult(q, asked, run, grade);
            int n = Interlocked.Increment(ref finished);
            log.WriteLine($"[{n}/{questions.Count}] {q.Id} {q.Family}: {run.Outcome} {(grade.Correct ? "right" : "wrong")}"
                + $"{(grade.Leaked ? " LEAK" : "")} calls {run.Calls.Count}{(run.Error is null ? "" : $" ({run.Error})")} | {agent.LastLimit}");
        });
        return [.. questions.Select(q => done[q.Id])];
    }

    /// <summary>Why a run is diagnostic only: anything short of the pre-registered run. Empty when it conforms.</summary>
    public static List<string> Nonconforming(Options options, IReadOnlyList<QuestionResult> results, IReadOnlyList<QuestionResult> mixed)
    {
        List<string> reasons = NonconformingTest(options, results.Count);
        if (mixed.Count != RegisteredMixed)
            reasons.Add($"{mixed.Count} mixed questions, not {RegisteredMixed}");
        return reasons;
    }

    /// <summary>The test-question half of <see cref="Nonconforming"/>, shared with the arms run.</summary>
    public static List<string> NonconformingTest(Options options, int questions)
    {
        List<string> reasons = [];
        if (options.Split != "test")
            reasons.Add($"split is {options.Split}, not test");
        if (options.Config != MemorySearchEval.RegisteredConfig)
            reasons.Add($"config is {options.Config}, not {MemorySearchEval.RegisteredConfig}");
        if (options.Model != MemorySearchEval.RegisteredModel)
            reasons.Add($"model is {options.Model}, not {MemorySearchEval.RegisteredModel}");
        if (options.LimitTasks is not null)
            reasons.Add("--limit-tasks was set");
        if (options.Ids.Count > 0)
            reasons.Add("--ids was set");
        if (options.Tables is { Count: > 0 })
            reasons.Add("--tables was set");
        if (questions != RegisteredTest)
            reasons.Add($"{questions} questions, not {RegisteredTest}");
        return reasons;
    }

    public static JsonObject Report(Options options, IReadOnlyList<QuestionResult> results, IReadOnlyList<QuestionResult> mixed)
    {
        List<string> nonconforming = Nonconforming(options, results, mixed);
        JsonObject families = [];
        foreach (string family in results.Select(r => r.Question.Family).Distinct().Order(StringComparer.Ordinal))
            families[family] = Group([.. results.Where(r => r.Question.Family == family)]);
        List<QuestionResult> restricted = [.. results.Where(r => r.Question.Restricted)];

        return new JsonObject
        {
            ["setup"] = options.Setup,
            ["mixedSetup"] = MixedSetupOf(options.Setup),
            ["tools"] = new JsonArray([.. ToolsOf(options.Setup).Select(t => (JsonNode)t)]),
            ["config"] = options.Config,
            ["split"] = options.Split,
            ["model"] = options.Model,
            ["effort"] = options.Effort,
            ["maxToolCalls"] = MaxToolCalls,
            ["all"] = Group(results),
            ["families"] = families,
            ["admin"] = Group([.. results.Where(r => !r.Question.Restricted)]),
            ["restricted"] = Group(restricted),
            ["restrictedLeaks"] = restricted.Count(r => r.Grade.Leaked),
            ["seenAsOf"] = new JsonObject
            {
                ["load1"] = Group([.. results.Where(r => r.Question.AsOfLoad == 1)]),
                ["load2"] = Group([.. results.Where(r => r.Question.AsOfLoad == 2)]),
            },
            ["mixed"] = Group(mixed),
            ["outcomes"] = Outcomes([.. results, .. mixed]),
            ["tokens"] = Tokens([.. results, .. mixed]),
            ["costPerQuestionUsd"] = results.Count == 0 ? 0 : Math.Round(results.Sum(r => r.Run.Usage.CostUsd) / results.Count, 4),
            ["conforming"] = nonconforming.Count == 0,
            ["diagnosticOnly"] = new JsonArray([.. nonconforming.Select(r => (JsonNode)r)]),
        };
    }

    private static JsonObject Group(IReadOnlyList<QuestionResult> group) => new()
    {
        ["questions"] = group.Count,
        ["correct"] = group.Count(r => r.Grade.Correct),
        ["accuracy"] = group.Count == 0 ? 0 : Math.Round((double)group.Count(r => r.Grade.Correct) / group.Count, 4),
        ["toolCalls"] = group.Count == 0 ? 0 : Math.Round(group.Average(r => r.Run.Calls.Count), 2),
    };

    private static JsonObject Outcomes(IEnumerable<QuestionResult> results)
    {
        JsonObject counts = [];
        foreach (IGrouping<AgentOutcome, QuestionResult> g in results.GroupBy(r => r.Run.Outcome).OrderBy(g => g.Key))
            counts[g.Key.ToString()] = g.Count();
        return counts;
    }

    /// <summary>Token totals, and what they would cost at API list price; the run itself is billed to the subscription.</summary>
    private static JsonObject Tokens(IReadOnlyList<QuestionResult> results)
    {
        TokenUsage total = results.Select(r => r.Run.Usage).Aggregate(TokenUsage.Zero, (a, b) => a.Add(b));
        return new JsonObject
        {
            ["runs"] = results.Count,
            ["input"] = total.Input,
            ["cacheWrite"] = total.CacheWrite,
            ["cacheRead"] = total.CacheRead,
            ["output"] = total.Output,
            ["apiListPriceUsd"] = Math.Round(total.CostUsd, 4),
        };
    }

    /// <summary>A tool's output is cut to this many characters in the run log; a whole 100k-row table would swamp it.</summary>
    public const int LoggedOutputChars = 2000;

    public static IEnumerable<string> RunLines(IEnumerable<QuestionResult> results) =>
        results.Select(r => RunLine(r).ToJsonString());

    /// <summary>One run as the run log records it.</summary>
    public static JsonObject RunLine(QuestionResult r) =>
            new()
            {
                ["id"] = r.Question.Id,
                ["family"] = r.Question.Family,
                ["user"] = r.Question.User,
                ["question"] = r.Asked,
                ["expected"] = JsonNode.Parse(r.Question.Answer.GetRawText()),
                ["answer"] = r.Run.AnswerValue is { } v ? JsonNode.Parse(v.GetRawText()) : null,
                ["correct"] = r.Grade.Correct,
                ["leaked"] = r.Grade.Leaked,
                ["outcome"] = r.Run.Outcome.ToString(),
                ["turns"] = r.Run.Turns,
                ["costUsd"] = Math.Round(r.Run.Usage.CostUsd, 4),
                ["calls"] = new JsonArray([.. r.Run.Calls.Select(c => (JsonNode?)new JsonObject
                {
                    ["tool"] = c.Name,
                    ["input"] = JsonSerializer.SerializeToNode(c.Input),
                    ["outputChars"] = c.Output.Length,
                    ["output"] = c.Output.Length <= LoggedOutputChars ? c.Output : c.Output[..LoggedOutputChars],
                })]),
                ["error"] = r.Run.Error,
            };
}
