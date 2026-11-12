using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Application.Tables;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Eval.Cli;
using Connapse.Eval.Metrics;
using Connapse.Eval.Model;
using Connapse.Eval.Runs.MemorySearch;
using Connapse.Eval.Systems;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Eval.Runs;

/// <summary>One nested-set question, as <c>eval/datasets/tables/nested/questions.jsonl</c> writes it.</summary>
public sealed record NestedQuestion(string Id, string Split, string Family, string Question, string AnswerType, JsonElement Answer, string File)
{
    public static List<NestedQuestion> ReadAll(string path) =>
        [.. System.IO.File.ReadLines(path).Where(l => l.Length > 0).Select(Parse)];

    public static NestedQuestion Parse(string line)
    {
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement q = doc.RootElement;
        return new NestedQuestion(q.GetProperty("id").GetString()!, q.GetProperty("split").GetString()!,
            q.GetProperty("family").GetString()!, q.GetProperty("question").GetString()!, q.GetProperty("answer_type").GetString()!,
            q.GetProperty("answer").Clone(), q.GetProperty("file").GetString()!);
    }
}

/// <summary>
/// CON-335's nested-files eval: four files (nested JSON, JSON Lines, XML and YAML) uploaded into one container, whose
/// parent and child tables Connapse loads at ingest, asked by an agent with the four table tools. The bar is "Bar for
/// nested files" in <c>eval/pass-bars.md</c>; its no-drop check on tables-v1 is a separate <c>tables-rag --setup tables</c> run.
/// </summary>
public sealed class TablesNestedEval(RepoPaths paths, TextWriter log)
{
    public static readonly string[] Families = ["lookup", "filter_count", "child_aggregate", "grandchild", "scalar_list", "json"];

    /// <summary>The registered test split: ten questions in each family.</summary>
    public const int RegisteredPerFamily = 10;

    public sealed record Options(
        string Config, string Split, string Model, string Effort, int? LimitTasks, IReadOnlyList<string> Ids, int Workers,
        string? OutPath, string? AgentDir = null, int? StallMinutes = null, long? MinFreeMemoryBytes = TablesLinkedEval.DefaultMinFreeMemory);

    public sealed record Result(NestedQuestion Question, AgentRun Run, bool Correct);

    /// <summary>The questions a run asks: one split, narrowed by ids and a limit.</summary>
    public static List<NestedQuestion> Questions(string nestedRoot, Options options, IReadOnlyList<EvalDocument> files)
    {
        List<NestedQuestion> questions = [.. NestedQuestion.ReadAll(Path.Combine(nestedRoot, "questions.jsonl")).Where(q => q.Split == options.Split)];
        if (options.Ids.Count > 0)
            questions = [.. questions.Where(q => options.Ids.Contains(q.Id))];
        if (options.LimitTasks is int limit)
            questions = [.. questions.Take(limit)];
        HashSet<string> known = [.. files.Select(f => f.FileName!)];
        int missing = questions.Count(q => !known.Contains(q.File));
        if (missing > 0)
            throw new InvalidDataException($"{missing} question(s) ask about a file that isn't among the {known.Count} uploaded.");
        return questions;
    }

    public async Task<int> RunAsync(Options options, CancellationToken ct)
    {
        string agentDir = Path.GetFullPath(options.AgentDir ?? Path.Combine(Path.GetTempPath(), "connapse-tables-nested-agent"));
        Directory.CreateDirectory(agentDir);
        MemorySearchEval.RequireNoProjectMemory(agentDir);

        string root = Path.Combine(paths.DatasetsRoot, TablesRagEval.Dataset, "nested");
        List<EvalDocument> files = Files(root);
        List<NestedQuestion> questions = Questions(root, options, files);
        if (OperatingSystem.IsWindows())
            System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;

        SystemConfig config = SystemConfig.Load(paths.EvalRoot, "connapse", options.Config);
        EmbeddingDiskCache embeddings = new(Path.Combine(paths.CacheRoot, "embeddings"));
        await using ConnapseSearchSystem system = await ConnapseSearchSystem.StartAsync(config, paths.WebContentRoot, embeddings, log, null, ct);
        if (options.StallMinutes is int stallMinutes)
            system.StallTimeout = TimeSpan.FromMinutes(stallMinutes);

        const string Name = "tables-nested";
        IndexReport indexed = await system.IndexAsync(new EvalDataset(Name, "v1", [], files, [], new Qrels()), IngestionWait.ThrowOnStall, ct);
        MemorySearchEval.RequireCompleteIndex(indexed, files.Count, system.ContainerOf(Name).DocMap.Count);
        Guid container = system.ContainerOf(Name).ContainerId;
        IReadOnlyList<TableInfo> tables;
        using (IServiceScope scope = system.Services.CreateScope())
            tables = await scope.ServiceProvider.GetRequiredService<ITableStore>().ListAsync(container, ct);
        log.WriteLine($"[{Name}] {files.Count} files indexed; {tables.Count} tables loaded: {string.Join("; ", tables.Select(t => t.Name))}");

        ClaimsPrincipal reader = McpToolbox.Reader(Guid.NewGuid(), "tables-nested-eval");
        McpToolbox toolbox = new(system.Services, reader, TablesRagEval.TableTools, MemorySearchEval.Pinned);
        await using ToolBridge bridge = new();
        await bridge.StartAsync(ct);
        ClaudeCliAgent agent = new(bridge, options.Model, options.Effort, agentDir, rules: TablesRagEval.Rules);

        ConcurrentDictionary<string, Result> done = new();
        int finished = 0;
        string prompt = TablesRagEval.BasePrompt(container);
        await Parallel.ForEachAsync(questions, new ParallelOptions { MaxDegreeOfParallelism = options.Workers, CancellationToken = ct }, async (q, token) =>
        {
            if (options.MinFreeMemoryBytes is long floor)
                await MemoryGate.WaitAsync(floor, MemoryGate.Available, TimeSpan.FromSeconds(30), log, token);
            AgentRun run = await agent.RunAsync(toolbox, prompt, q.Question, token);
            Result result = Grade(q, run);
            done[q.Id] = result;
            int n = Interlocked.Increment(ref finished);
            log.WriteLine($"[{n}/{questions.Count}] {q.Id} {q.Family}: {run.Outcome} {(result.Correct ? "right" : "wrong")}"
                + $" calls {run.Calls.Count}{(run.Error is null ? "" : $" ({run.Error})")} | {agent.LastLimit}");
        });
        List<Result> results = [.. questions.Select(q => done[q.Id])];

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

    /// <summary>The nested-set files, each uploaded under its own name as a user's file would be.</summary>
    public static List<EvalDocument> Files(string nestedRoot, string set = "nested") =>
        [.. Directory.GetFiles(Path.Combine(nestedRoot, "docs")).Order(StringComparer.Ordinal).Select(path =>
            new EvalDocument(set + "/docs/" + Path.GetFileName(path), DocumentKind.File, Path.GetFileNameWithoutExtension(path), null, null,
                new Dictionary<string, string>(), path)
            {
                FileName = Path.GetFileName(path),
                Folder = "/data",
            })];

    public static Result Grade(NestedQuestion question, AgentRun run) =>
        new(question, run, run.AnswerValue is { } value && TableAnswerGrader.Grade(value, question.Answer, question.AnswerType));

    /// <summary>Why a run is diagnostic only: anything short of the registered run. Empty when it conforms.</summary>
    public static List<string> Nonconforming(Options options, IReadOnlyList<Result> results)
    {
        List<string> reasons = [];
        if (options.Config != MemorySearchEval.RegisteredConfig)
            reasons.Add($"config is {options.Config}, not {MemorySearchEval.RegisteredConfig}");
        if (options.Model != MemorySearchEval.RegisteredModel)
            reasons.Add($"model is {options.Model}, not {MemorySearchEval.RegisteredModel}");
        if (options.Split != "test")
            reasons.Add($"split is {options.Split}, not test");
        if (options.LimitTasks is not null)
            reasons.Add("--limit-tasks was set");
        if (options.Ids.Count > 0)
            reasons.Add("--ids was set");
        foreach (string family in Families)
        {
            int count = results.Count(r => r.Question.Family == family);
            if (count != RegisteredPerFamily)
                reasons.Add($"{count} {family} questions, not {RegisteredPerFamily}");
        }
        return reasons;
    }

    public static JsonObject Report(Options options, IReadOnlyList<Result> results)
    {
        List<string> nonconforming = Nonconforming(options, results);
        int correct = results.Count(r => r.Correct);
        JsonObject families = [];
        bool everyFamily = true;
        foreach (string family in Families)
        {
            List<Result> of = [.. results.Where(r => r.Question.Family == family)];
            int right = of.Count(r => r.Correct);
            bool pass = of.Count > 0 && right * 4 >= of.Count * 3;
            everyFamily &= pass;
            families[family] = new JsonObject { ["questions"] = of.Count, ["correct"] = right, ["pass"] = pass };
        }
        JsonObject outcomes = [];
        foreach (IGrouping<AgentOutcome, Result> g in results.GroupBy(r => r.Run.Outcome).OrderBy(g => g.Key))
            outcomes[g.Key.ToString()] = g.Count();

        return new JsonObject
        {
            ["tools"] = new JsonArray([.. TablesRagEval.TableTools.Select(t => (JsonNode)t)]),
            ["split"] = options.Split,
            ["config"] = options.Config,
            ["model"] = options.Model,
            ["effort"] = options.Effort,
            ["maxToolCalls"] = TablesRagEval.MaxToolCalls,
            ["questions"] = results.Count,
            ["correct"] = correct,
            ["toolCalls"] = results.Count == 0 ? 0 : Math.Round(results.Average(r => r.Run.Calls.Count), 2),
            ["costPerQuestionUsd"] = results.Count == 0 ? 0 : Math.Round(results.Average(r => r.Run.Usage.CostUsd), 4),
            ["outcomes"] = outcomes,
            ["families"] = families,
            ["bar"] = new JsonObject
            {
                ["1_overall"] = new JsonObject
                {
                    ["correct"] = correct,
                    ["rate"] = results.Count == 0 ? 0 : Math.Round((double)correct / results.Count, 4),
                    ["pass"] = results.Count > 0 && correct * 20 >= results.Count * 17,
                },
                ["2_everyFamily"] = new JsonObject { ["pass"] = everyFamily },
                ["3_noDrop"] = "a separate run: tables-rag --setup tables --split test, at least 176 of 180",
                ["4_serverTokens"] = "0 by construction: the table tools call no model",
            },
            ["conforming"] = nonconforming.Count == 0,
            ["diagnosticOnly"] = new JsonArray([.. nonconforming.Select(r => (JsonNode)r)]),
        };
    }

    /// <summary>One run as the run log records it.</summary>
    public static JsonObject RunLine(Result r) => new()
    {
        ["id"] = r.Question.Id,
        ["family"] = r.Question.Family,
        ["file"] = r.Question.File,
        ["question"] = r.Question.Question,
        ["expected"] = JsonNode.Parse(r.Question.Answer.GetRawText()),
        ["answer"] = r.Run.AnswerValue is { } v ? JsonNode.Parse(v.GetRawText()) : null,
        ["correct"] = r.Correct,
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
