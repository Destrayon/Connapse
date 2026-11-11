using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Connapse.Application.Ports;
using Connapse.Application.Search;
using Connapse.Application.Tables;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Eval.Cli;
using Connapse.Eval.Metrics;
using Connapse.Eval.Model;
using Connapse.Eval.Runs.MemorySearch;
using Connapse.Eval.Systems;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Connapse.Eval.Runs;

/// <summary>One linked-set question, as <c>eval/datasets/tables/linked/questions.jsonl</c> writes it.</summary>
public sealed record LinkedQuestion(string Id, string Kind, string Question, string AnswerType, JsonElement Answer, string Document)
{
    public bool IsTable => Kind == "table";

    public static List<LinkedQuestion> ReadAll(string path) =>
        [.. File.ReadLines(path).Where(l => l.Length > 0).Select(Parse)];

    public static LinkedQuestion Parse(string line)
    {
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement q = doc.RootElement;
        return new LinkedQuestion(q.GetProperty("id").GetString()!, q.GetProperty("kind").GetString()!,
            q.GetProperty("question").GetString()!, q.GetProperty("answer_type").GetString()!, q.GetProperty("answer").Clone(),
            q.GetProperty("document").GetString()!);
    }
}

/// <summary>
/// CON-312's passage-link eval: the ten linked-set reports (DOCX and PDF) in one documents container, whose tables
/// Connapse loads at ingest, asked by an agent with the six tools. both-linked is Connapse as it ships, where a passage
/// and a document name the tables found in them; both-unlinked is the same build with those names turned off, so the
/// tables are reachable only through <c>table_list</c>. The bar is "Bar for passage links" in <c>eval/pass-bars.md</c>.
/// </summary>
public sealed partial class TablesLinkedEval(RepoPaths paths, TextWriter log)
{
    public const string LinkedSetup = "both-linked";
    public const string UnlinkedSetup = "both-unlinked";

    public static readonly string[] Setups = [LinkedSetup, UnlinkedSetup];

    public static readonly string[] Tools = [.. MemorySearchEval.SearchTools, .. TablesRagEval.TableTools];

    public const int RegisteredTable = 20;
    public const int RegisteredProse = 10;

    public sealed record Options(
        string Config, string Model, string Effort, int? LimitTasks, IReadOnlyList<string> Ids, int Workers, string? OutPath,
        string? AgentDir = null, int? StallMinutes = null);

    public sealed record Result(LinkedQuestion Question, string Setup, AgentRun Run, bool Correct, bool FollowedLink, bool TableCall);

    public async Task<int> RunAsync(Options options, CancellationToken ct)
    {
        string agentDir = Path.GetFullPath(options.AgentDir ?? Path.Combine(Path.GetTempPath(), "connapse-tables-linked-agent"));
        Directory.CreateDirectory(agentDir);
        MemorySearchEval.RequireNoProjectMemory(agentDir);

        string root = Path.Combine(paths.DatasetsRoot, TablesRagEval.Dataset, "linked");
        List<LinkedQuestion> questions = LinkedQuestion.ReadAll(Path.Combine(root, "questions.jsonl"));
        if (options.Ids.Count > 0)
            questions = [.. questions.Where(q => options.Ids.Contains(q.Id))];
        if (options.LimitTasks is int limit)
            questions = [.. questions.Take(limit)];
        List<EvalDocument> reports = Reports(root);

        SystemConfig config = SystemConfig.Load(paths.EvalRoot, "connapse", options.Config);
        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        await using ConnapseSearchSystem system = await ConnapseSearchSystem.StartAsync(config, paths.WebContentRoot, embeddings, log, null, ct);
        if (options.StallMinutes is int stallMinutes)
            system.StallTimeout = TimeSpan.FromMinutes(stallMinutes);

        const string Name = "tables-linked";
        IndexReport indexed = await system.IndexAsync(new EvalDataset(Name, "v1", [], reports, [], new Qrels()), IngestionWait.ThrowOnStall, ct);
        MemorySearchEval.RequireCompleteIndex(indexed, reports.Count, system.ContainerOf(Name).DocMap.Count);
        Guid container = system.ContainerOf(Name).ContainerId;
        IReadOnlyList<TableInfo> tables;
        using (IServiceScope scope = system.Services.CreateScope())
            tables = await scope.ServiceProvider.GetRequiredService<ITableStore>().ListAsync(container, ct);
        log.WriteLine($"[{Name}] {reports.Count} reports indexed; {tables.Count} tables loaded: {string.Join("; ", tables.Select(t => t.Name))}");

        ClaimsPrincipal reader = McpToolbox.Reader(Guid.NewGuid(), "tables-linked-eval");
        Dictionary<string, McpToolbox> toolboxes = new(StringComparer.Ordinal)
        {
            [LinkedSetup] = new(system.Services, reader, Tools, MemorySearchEval.Pinned),
            [UnlinkedSetup] = new(system.Services, reader, Tools, MemorySearchEval.Pinned, Unlinked),
        };
        await using ToolBridge bridge = new();
        await bridge.StartAsync(ct);
        ClaudeCliAgent agent = new(bridge, options.Model, options.Effort, agentDir, rules: TablesRagEval.Rules);

        // Each question is asked of both setups, in an order that alternates by question, so neither always goes first.
        List<(LinkedQuestion Question, string Setup)> runs = [.. questions.SelectMany((q, i) =>
            (i % 2 == 0 ? Setups : Setups.Reverse()).Select(s => (q, s)))];
        ConcurrentDictionary<(string, string), Result> done = new();
        int finished = 0;
        string prompt = TablesRagEval.BasePrompt(container);
        await Parallel.ForEachAsync(runs, new ParallelOptions { MaxDegreeOfParallelism = options.Workers, CancellationToken = ct }, async (r, token) =>
        {
            AgentRun run = await agent.RunAsync(toolboxes[r.Setup], prompt, r.Question.Question, token);
            Result result = Grade(r.Question, r.Setup, run);
            done[(r.Question.Id, r.Setup)] = result;
            int n = Interlocked.Increment(ref finished);
            log.WriteLine($"[{n}/{runs.Count}] {r.Question.Id} {r.Question.Kind} {r.Setup}: {run.Outcome} {(result.Correct ? "right" : "wrong")}"
                + $"{(result.FollowedLink ? " followed" : "")} calls {run.Calls.Count}{(run.Error is null ? "" : $" ({run.Error})")} | {agent.LastLimit}");
        });
        List<Result> results = [.. runs.Select(r => done[(r.Question.Id, r.Setup)])];

        JsonObject report = Report(options, results);
        report["tablesLoaded"] = new JsonArray([.. tables.Select(t => (JsonNode)t.Name)]);
        report["subscription"] = new JsonObject
        {
            ["atFirstRun"] = agent.FirstLimit?.ToString(),
            ["atLastRun"] = agent.LastLimit?.ToString(),
        };
        string json = report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (options.OutPath is not null)
        {
            await File.WriteAllTextAsync(options.OutPath, json + "\n", ct);
            await File.WriteAllLinesAsync(Path.ChangeExtension(options.OutPath, ".runs.jsonl"), results.Select(r => RunLine(r).ToJsonString()), ct);
        }
        log.WriteLine(json);
        return 0;
    }

    /// <summary>The linked-set reports, each uploaded under its own name as a user's file would be.</summary>
    public static List<EvalDocument> Reports(string linkedRoot) =>
        [.. Directory.GetFiles(Path.Combine(linkedRoot, "docs")).Order(StringComparer.Ordinal).Select(path =>
            new EvalDocument("linked/docs/" + Path.GetFileName(path), DocumentKind.File, Path.GetFileNameWithoutExtension(path), null, null,
                new Dictionary<string, string>(), path)
            {
                FileName = Path.GetFileName(path),
                Folder = "/reports",
            })];

    /// <summary>
    /// both-unlinked's operations: search without the passage-table step, and a document view whose table list is
    /// refused, which <c>get_document</c> prints as no tables line. The tables themselves are untouched.
    /// </summary>
    public static readonly IReadOnlyDictionary<Type, Func<IServiceProvider, object>> Unlinked = new Dictionary<Type, Func<IServiceProvider, object>>
    {
        [typeof(SearchKnowledge)] = sp => new SearchKnowledge(
            sp.GetRequiredService<IContainerStore>(), sp.GetRequiredService<ISourceStore>(), sp.GetRequiredService<ISourceVisibility>(),
            sp.GetRequiredService<IKnowledgeSearch>(), sp.GetRequiredService<IOptionsMonitor<SearchSettings>>(),
            sp.GetRequiredService<IOperationAuthorizer>(), sp.GetService<ILocalModelMonitor>(), sp.GetService<IDocumentStore>(),
            passageTables: null),
        [typeof(GetFileTables)] = sp => new GetFileTables(
            sp.GetRequiredService<IContainerStore>(), sp.GetRequiredService<IDocumentStore>(), sp.GetRequiredService<Connapse.Application.Ports.IDocumentAccess>(),
            sp.GetRequiredService<ITableStore>(), new DenyAll()),
    };

    private sealed class DenyAll : IOperationAuthorizer
    {
        public Task<bool> AllowsAsync(ClaimsPrincipal user, string policy) => Task.FromResult(false);
    }

    public static Result Grade(LinkedQuestion question, string setup, AgentRun run)
    {
        bool correct = run.AnswerValue is { } value && TableAnswerGrader.Grade(value, question.Answer, question.AnswerType);
        return new Result(question, setup, run, correct, FollowedLink(run.Calls), TableCall(run.Calls));
    }

    private static readonly string[] QueryTools = ["table_read", "table_aggregate"];

    public static bool TableCall(IReadOnlyList<ToolTrace> calls) =>
        calls.Any(c => c.Name.StartsWith("table_", StringComparison.Ordinal));

    /// <summary>
    /// The bar's followed link: a successful <c>table_read</c> or <c>table_aggregate</c> on a table id that first
    /// appeared in the run in a <c>search_knowledge</c> or <c>get_document</c> answer, with no <c>table_list</c> before it.
    /// </summary>
    public static bool FollowedLink(IReadOnlyList<ToolTrace> calls) => FollowedAt(calls) is not null;

    /// <summary>The index of the call that followed a link, or null.</summary>
    public static int? FollowedAt(IReadOnlyList<ToolTrace> calls)
    {
        HashSet<string> linked = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> elsewhere = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < calls.Count; i++)
        {
            ToolTrace call = calls[i];
            if (call.Name == "table_list")
                return null;
            if (QueryTools.Contains(call.Name) && TableIdOf(call) is string id && linked.Contains(id) && Succeeded(call))
                return i;
            HashSet<string> into = MemorySearchEval.SearchTools.Contains(call.Name) ? linked : elsewhere;
            foreach (Match m in Ids().Matches(call.Output))
                if (!elsewhere.Contains(m.Value))
                    into.Add(m.Value);
        }
        return null;
    }

    private static bool Succeeded(ToolTrace call) =>
        !call.Output.TrimStart().StartsWith("Error", StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>table</c> a read or aggregate request names, from its <c>request</c> object or a JSON string of one.</summary>
    public static string? TableIdOf(ToolTrace call)
    {
        if (!call.Input.TryGetValue("request", out JsonElement request))
            return null;
        if (request.ValueKind == JsonValueKind.String)
        {
            try
            {
                using JsonDocument parsed = JsonDocument.Parse(request.GetString()!);
                return TableOf(parsed.RootElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return TableOf(request);
    }

    private static string? TableOf(JsonElement request) =>
        request.ValueKind == JsonValueKind.Object && request.TryGetProperty("table", out JsonElement t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()!.Trim()
            : null;

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex Ids();

    /// <summary>Why a run is diagnostic only: anything short of the registered run. Empty when it conforms.</summary>
    public static List<string> Nonconforming(Options options, IReadOnlyList<Result> results)
    {
        List<string> reasons = [];
        if (options.Config != MemorySearchEval.RegisteredConfig)
            reasons.Add($"config is {options.Config}, not {MemorySearchEval.RegisteredConfig}");
        if (options.Model != MemorySearchEval.RegisteredModel)
            reasons.Add($"model is {options.Model}, not {MemorySearchEval.RegisteredModel}");
        if (options.LimitTasks is not null)
            reasons.Add("--limit-tasks was set");
        if (options.Ids.Count > 0)
            reasons.Add("--ids was set");
        int table = results.Where(r => r.Setup == LinkedSetup).Count(r => r.Question.IsTable);
        int prose = results.Where(r => r.Setup == LinkedSetup).Count(r => !r.Question.IsTable);
        if (table != RegisteredTable || prose != RegisteredProse)
            reasons.Add($"{table} table and {prose} prose questions, not {RegisteredTable} and {RegisteredProse}");
        return reasons;
    }

    public static JsonObject Report(Options options, IReadOnlyList<Result> results)
    {
        List<Result> Of(string setup) => [.. results.Where(r => r.Setup == setup)];
        List<Result> linked = Of(LinkedSetup);
        List<Result> unlinked = Of(UnlinkedSetup);
        List<string> nonconforming = Nonconforming(options, results);

        int linkedTable = linked.Count(r => r.Question.IsTable);
        int followed = linked.Count(r => r.Question.IsTable && r.FollowedLink);
        int tableGain = Correct(linked, true) - Correct(unlinked, true);
        int proseTableCalls = linked.Count(r => !r.Question.IsTable && r.TableCall);
        double linkedCost = Cost(linked);
        double unlinkedCost = Cost(unlinked);

        JsonObject setups = [];
        foreach (string setup in Setups)
            setups[setup] = Summary(Of(setup));

        return new JsonObject
        {
            ["tools"] = new JsonArray([.. Tools.Select(t => (JsonNode)t)]),
            ["config"] = options.Config,
            ["model"] = options.Model,
            ["effort"] = options.Effort,
            ["maxToolCalls"] = TablesRagEval.MaxToolCalls,
            ["setups"] = setups,
            ["bar"] = new JsonObject
            {
                ["1_followedLink"] = new JsonObject
                {
                    ["followed"] = followed,
                    ["tableQuestions"] = linkedTable,
                    ["rate"] = linkedTable == 0 ? 0 : Math.Round((double)followed / linkedTable, 4),
                    ["pass"] = linkedTable > 0 && followed * 5 >= linkedTable * 4,
                },
                ["2_helps"] = new JsonObject
                {
                    ["allCorrectLinked"] = Correct(linked, null),
                    ["allCorrectUnlinked"] = Correct(unlinked, null),
                    ["tableGain"] = tableGain,
                    ["pass"] = Correct(linked, null) >= Correct(unlinked, null) && tableGain >= 4,
                },
                ["3_noDistraction"] = new JsonObject
                {
                    ["proseWithTableCall"] = proseTableCalls,
                    ["proseCorrectLinked"] = Correct(linked, false),
                    ["proseCorrectUnlinked"] = Correct(unlinked, false),
                    ["pass"] = proseTableCalls <= 2 && Correct(linked, false) >= Correct(unlinked, false),
                },
                ["4_noLeak"] = "measured by tests, not this run: DocumentTableLinkTests and the PassageTables unit tests",
                ["5_cost"] = new JsonObject
                {
                    ["linkedPerQuestionUsd"] = Math.Round(linkedCost, 4),
                    ["unlinkedPerQuestionUsd"] = Math.Round(unlinkedCost, 4),
                    ["ratio"] = unlinkedCost == 0 ? 0 : Math.Round(linkedCost / unlinkedCost, 3),
                    ["pass"] = unlinkedCost > 0 && linkedCost <= unlinkedCost * 1.2,
                },
            },
            ["conforming"] = nonconforming.Count == 0,
            ["diagnosticOnly"] = new JsonArray([.. nonconforming.Select(r => (JsonNode)r)]),
        };
    }

    private static int Correct(IEnumerable<Result> results, bool? table) =>
        results.Count(r => r.Correct && (table is null || r.Question.IsTable == table));

    private static double Cost(IReadOnlyList<Result> results) =>
        results.Count == 0 ? 0 : results.Sum(r => r.Run.Usage.CostUsd) / results.Count;

    private static JsonObject Summary(IReadOnlyList<Result> results)
    {
        JsonObject outcomes = [];
        foreach (IGrouping<AgentOutcome, Result> g in results.GroupBy(r => r.Run.Outcome).OrderBy(g => g.Key))
            outcomes[g.Key.ToString()] = g.Count();
        return new JsonObject
        {
            ["questions"] = results.Count,
            ["correct"] = Correct(results, null),
            ["tableCorrect"] = Correct(results, true),
            ["proseCorrect"] = Correct(results, false),
            ["followedLink"] = results.Count(r => r.Question.IsTable && r.FollowedLink),
            ["usedTableList"] = results.Count(r => r.Run.Calls.Any(c => c.Name == "table_list")),
            ["proseWithTableCall"] = results.Count(r => !r.Question.IsTable && r.TableCall),
            ["toolCalls"] = results.Count == 0 ? 0 : Math.Round(results.Average(r => r.Run.Calls.Count), 2),
            ["costPerQuestionUsd"] = Math.Round(Cost(results), 4),
            ["outcomes"] = outcomes,
        };
    }

    /// <summary>One run as the run log records it, with the call that followed a link marked.</summary>
    public static JsonObject RunLine(Result r)
    {
        int? followedAt = FollowedAt(r.Run.Calls);
        return new JsonObject
        {
            ["id"] = r.Question.Id,
            ["kind"] = r.Question.Kind,
            ["setup"] = r.Setup,
            ["question"] = r.Question.Question,
            ["expected"] = JsonNode.Parse(r.Question.Answer.GetRawText()),
            ["answer"] = r.Run.AnswerValue is { } v ? JsonNode.Parse(v.GetRawText()) : null,
            ["correct"] = r.Correct,
            ["followedLink"] = r.FollowedLink,
            ["followedAtCall"] = followedAt,
            ["tableCall"] = r.TableCall,
            ["outcome"] = r.Run.Outcome.ToString(),
            ["turns"] = r.Run.Turns,
            ["costUsd"] = Math.Round(r.Run.Usage.CostUsd, 4),
            ["calls"] = new JsonArray([.. r.Run.Calls.Select(c => (JsonNode?)new JsonObject
            {
                ["tool"] = c.Name,
                ["input"] = JsonSerializer.SerializeToNode(c.Input),
                ["outputChars"] = c.Output.Length,
                ["output"] = c.Output.Length <= TablesRagEval.LoggedOutputChars ? c.Output : c.Output[..TablesRagEval.LoggedOutputChars],
            })]),
            ["error"] = r.Run.Error,
        };
    }
}
