using System.Text.Json;
using Connapse.Eval.Runs;
using Connapse.Eval.Runs.MemorySearch;
using Connapse.Eval.Statistics;
using Connapse.Eval.Systems;
using FluentAssertions;

namespace Connapse.Eval.Tests.Runs;

[Trait("Category", "Unit")]
public class MemorySearchTests
{
    private const string GoldUuid = "11111111-1111-1111-1111-111111111111";
    private const string StaleUuid = "22222222-2222-2222-2222-222222222222";
    private const string OtherUuid = "33333333-3333-3333-3333-333333333333";

    private static readonly DocumentIndex Index = new(
        new Dictionary<string, string> { [GoldUuid] = "ms-0001", [StaleUuid] = "ms-0002", [OtherUuid] = "ms-0003" },
        new Dictionary<string, string> { ["ms-0001"] = "Finance Identity Standard.txt", ["ms-0002"] = "Finance Access Policy.txt", ["ms-0003"] = "Other.txt" });

    private static SearchTask Task(string kind = "current") =>
        new("t-1", kind, "test", false, "How often do Finance passwords rotate?", ["ms-0001"], ["ms-0002"], "270 days", "120 days");

    private static Dictionary<string, JsonElement> Input(object o) =>
        JsonSerializer.SerializeToElement(o).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static ModelReply Calls(params AgentToolCall[] calls) => new(calls, "tool_use", new TokenUsage(10, 5, 0, 0));

    private static AgentToolCall Search(string id = "s") => new(id, "search_knowledge", Input(new { query = "q", containerId = "c" }));

    private static AgentToolCall AnswerCall(string answer, params string[] ids) => new("a", "answer", Input(new { answer, documentIds = ids }));

    private sealed class ScriptedModel(params Func<IReadOnlyList<AgentMessage>, ModelReply>[] turns) : IAgentModel
    {
        private int _turn;
        public List<IReadOnlyList<ToolSpec>> SeenTools { get; } = [];

        public Task<ModelReply> ReplyAsync(string system, IReadOnlyList<ToolSpec> tools, IReadOnlyList<AgentMessage> messages, CancellationToken ct)
        {
            SeenTools.Add(tools);
            return System.Threading.Tasks.Task.FromResult(turns[Math.Min(_turn++, turns.Length - 1)](messages));
        }
    }

    private sealed class FakeTools(string output = $"--- Result 1 ---\nDocumentId: {GoldUuid}\n") : IToolbox
    {
        public int Calls { get; private set; }
        public IReadOnlyList<ToolSpec> Tools { get; } = [new("search_knowledge", "search", new Dictionary<string, JsonElement>(), [])];

        public Task<string> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
        {
            Calls++;
            return name == "boom" ? throw new InvalidOperationException("tool broke") : System.Threading.Tasks.Task.FromResult(output);
        }
    }

    [Fact]
    public async Task RunAsync_SearchThenAnswer_ReturnsAnswerCitationsAndTrace()
    {
        ScriptedModel model = new(_ => Calls(Search()), _ => Calls(AnswerCall("270 days", GoldUuid)));

        AgentRun run = await AgentLoop.RunAsync(model, new FakeTools(), "sys", "q?", CancellationToken.None);

        run.Outcome.Should().Be(AgentOutcome.Answered);
        run.Answer.Should().Be("270 days");
        run.Cited.Should().Equal(GoldUuid);
        run.Calls.Should().ContainSingle().Which.Name.Should().Be("search_knowledge");
        run.Usage.Input.Should().Be(20);
        run.Turns.Should().Be(2);
        model.SeenTools[0].Select(t => t.Name).Should().Equal("search_knowledge", "answer");
    }

    [Fact]
    public async Task RunAsync_ToolResult_GoesBackInTheNextUserMessage()
    {
        IReadOnlyList<AgentMessage>? second = null;
        ScriptedModel model = new(_ => Calls(Search("call-1")), m => { second = m; return Calls(AnswerCall("x")); });

        await AgentLoop.RunAsync(model, new FakeTools("found it"), "sys", "q?", CancellationToken.None);

        second.Should().HaveCount(3);
        second![1].Role.Should().Be(AgentRole.Assistant);
        second[2].Blocks.Should().ContainSingle().Which.Should().Be(new AgentToolResult("call-1", "found it"));
    }

    [Fact]
    public async Task RunAsync_AnswerBesideOtherCalls_EndsWithoutRunningThem()
    {
        FakeTools tools = new();
        ScriptedModel model = new(_ => Calls(Search(), AnswerCall("x", GoldUuid)));

        AgentRun run = await AgentLoop.RunAsync(model, tools, "sys", "q?", CancellationToken.None);

        run.Outcome.Should().Be(AgentOutcome.Answered);
        tools.Calls.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_ModelStopsWithoutATool_IsNoAnswer()
    {
        ScriptedModel model = new(_ => new ModelReply([new AgentText("I think it's 270 days.")], "end_turn", TokenUsage.Zero));

        AgentRun run = await AgentLoop.RunAsync(model, new FakeTools(), "sys", "q?", CancellationToken.None);

        run.Outcome.Should().Be(AgentOutcome.NoAnswer);
        run.Cited.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_MoreThanTwelveCalls_IsOutOfCalls()
    {
        FakeTools tools = new();
        ScriptedModel model = new(_ => Calls(Search()));

        AgentRun run = await AgentLoop.RunAsync(model, tools, "sys", "q?", CancellationToken.None);

        run.Outcome.Should().Be(AgentOutcome.OutOfCalls);
        tools.Calls.Should().Be(AgentLoop.MaxToolCalls);
    }

    [Fact]
    public async Task RunAsync_Refusal_IsRefused()
    {
        ScriptedModel model = new(_ => new ModelReply([], "refusal", TokenUsage.Zero));

        (await AgentLoop.RunAsync(model, new FakeTools(), "sys", "q?", CancellationToken.None)).Outcome.Should().Be(AgentOutcome.Refused);
    }

    [Fact]
    public async Task RunAsync_ModelOrToolThrows_IsErrorWithReason()
    {
        ScriptedModel throwing = new(_ => throw new HttpRequestException("503"));
        ScriptedModel callsBroken = new(_ => Calls(new AgentToolCall("b", "boom", Input(new { }))));

        AgentRun modelError = await AgentLoop.RunAsync(throwing, new FakeTools(), "sys", "q?", CancellationToken.None);
        AgentRun toolError = await AgentLoop.RunAsync(callsBroken, new FakeTools(), "sys", "q?", CancellationToken.None);

        modelError.Outcome.Should().Be(AgentOutcome.Error);
        modelError.Error.Should().Contain("503");
        toolError.Outcome.Should().Be(AgentOutcome.Error);
        toolError.Error.Should().Contain("tool broke");
    }

    [Fact]
    public void ReadAnswer_LoneStringId_CountsAsOneCitation()
    {
        (string? answer, List<string> cited) = AgentLoop.ReadAnswer(Input(new { answer = "x", documentIds = GoldUuid }));

        answer.Should().Be("x");
        cited.Should().Equal(GoldUuid);
    }

    [Fact]
    public void Describe_SearchKnowledge_ExposesOnlyTheAgentsParameters()
    {
        ToolSpec spec = McpToolbox.Describe(McpToolbox.Find("search_knowledge"));

        spec.Name.Should().Be("search_knowledge");
        spec.Description.Should().NotBeEmpty();
        spec.Properties.Keys.Should().BeEquivalentTo(["query", "containerId", "mode", "topK", "path", "minScore"]);
        spec.Required.Should().Equal("query", "containerId");
        spec.Properties["topK"].GetProperty("type").GetString().Should().Be("integer");
        spec.Properties["minScore"].GetProperty("type").GetString().Should().Be("number");
    }

    [Fact]
    public void Describe_MemoryRecall_HasNoRequiredArgumentBeyondContainer()
    {
        ToolSpec spec = McpToolbox.Describe(McpToolbox.Find("memory_recall"));

        spec.Required.Should().Equal("containerId");
        spec.Properties["includeHistory"].GetProperty("type").GetString().Should().Be("boolean");
    }

    [Fact]
    public void Find_UnknownTool_Throws()
    {
        FluentActions.Invoking(() => McpToolbox.Find("no_such_tool")).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(GoldUuid, "ms-0001")]
    [InlineData("11111111-1111-1111-1111-111111111111 ", "ms-0001")]
    [InlineData("Finance Identity Standard.txt", "ms-0001")]
    [InlineData("/finance identity standard.txt", "ms-0001")]
    [InlineData("Finance Identity Standard", "ms-0001")]
    [InlineData("44444444-4444-4444-4444-444444444444", null)]
    [InlineData("ms-0001", null)]
    public void Resolve_Citation_MapsToDatasetId(string cited, string? expected)
    {
        Index.Resolve(cited).Should().Be(expected);
    }

    [Fact]
    public void Ranked_SearchOutput_ListsDocumentsInOrderOnce()
    {
        string output = $"--- Result 1 ---\nDocumentId: {OtherUuid}\n--- Result 2 ---\nDocumentId: {GoldUuid}\n--- Result 3 ---\nDocumentId: {OtherUuid}\n";

        Index.Ranked(output).Should().Equal("ms-0003", "ms-0001");
    }

    private static AgentRun Run(AgentOutcome outcome, string? answer, string[] cited, params ToolTrace[] calls) =>
        new(outcome, answer, cited, calls, new TokenUsage(100, 10, 50, 0), 2, null);

    private static ToolTrace Trace(string name, string output) => new(name, new Dictionary<string, JsonElement>(), output);

    [Fact]
    public void Score_GoldCitedWithoutStale_ScoresOne()
    {
        AgentRun run = Run(AgentOutcome.Answered, "270 days", [GoldUuid],
            Trace("memory_recall", "{}"), Trace("search_knowledge", $"DocumentId: {OtherUuid}\nDocumentId: {GoldUuid}\n"));

        RunScore score = MemorySearchScoring.Score(Task(), run, Index);

        score.Score.Should().Be(1);
        score.AnswerRight.Should().BeTrue();
        score.SawGold.Should().BeTrue();
        score.FirstQueryMrr.Should().Be(0.5);
        score.Searches.Should().Be(1);
        score.MemoryCalls.Should().Be(1);
        score.ToolCalls.Should().Be(2);
    }

    [Fact]
    public void Score_StaleCitedBesideGold_ScoresZero()
    {
        RunScore score = MemorySearchScoring.Score(Task(), Run(AgentOutcome.Answered, "270 days, was 120 days", [GoldUuid, StaleUuid]), Index);

        score.Score.Should().Be(0);
        score.CitedStale.Should().BeTrue();
        score.AnswerRight.Should().BeFalse();
    }

    [Fact]
    public void Score_NoAnswerOutcome_ScoresZeroEvenWithCitations()
    {
        MemorySearchScoring.Score(Task(), Run(AgentOutcome.OutOfCalls, null, [GoldUuid]), Index).Score.Should().Be(0);
    }

    [Fact]
    public void Score_CitationByFileName_Counts()
    {
        MemorySearchScoring.Score(Task(), Run(AgentOutcome.Answered, "270 days", ["Finance Identity Standard.txt"]), Index).Score.Should().Be(1);
    }

    private static PairedComparison C(double mean, double low, double high, double p = 0.01) => new(64, mean, low, high, p, p, 1, 0.1);

    [Fact]
    public void Decide_ClearWinOnMAndHoldsOnH_IsKeep()
    {
        MemorySearchScoring.Decide(C(0.20, 0.08, 0.32), C(-0.02, -0.08, 0.04), C(0.10, 0.03, 0.17)).Should().Be(Verdict.Keep);
    }

    [Fact]
    public void Decide_GainBelowPointFifteen_IsNoBetter()
    {
        MemorySearchScoring.Decide(C(0.14, 0.05, 0.23), C(0, -0.05, 0.05), C(0.08, 0.02, 0.14)).Should().Be(Verdict.NoBetter);
    }

    [Fact]
    public void Decide_PermutationPNotSignificant_IsNoBetter()
    {
        MemorySearchScoring.Decide(C(0.20, 0.01, 0.39, p: 0.06), C(0, -0.05, 0.05), C(0.1, 0.0, 0.2)).Should().Be(Verdict.NoBetter);
    }

    [Fact]
    public void Decide_WinOnMButHHurtBeyondMargin_IsWorse()
    {
        MemorySearchScoring.Decide(C(0.20, 0.08, 0.32), C(-0.06, -0.12, 0.0), C(0.08, 0.01, 0.15)).Should().Be(Verdict.Worse);
    }

    [Fact]
    public void Decide_HLowerBoundExactlyAtMargin_IsNotKeep()
    {
        MemorySearchScoring.Decide(C(0.20, 0.08, 0.32), C(-0.04, -0.10, 0.02), C(0.08, 0.01, 0.15)).Should().Be(Verdict.Worse);
    }

    [Fact]
    public void Decide_AllTasksIntervalBelowZero_IsWorse()
    {
        MemorySearchScoring.Decide(C(-0.05, -0.12, 0.02), C(-0.01, -0.06, 0.04), C(-0.06, -0.10, -0.01)).Should().Be(Verdict.Worse);
    }

    [Fact]
    public void Decide_TieEverywhere_IsNoBetter()
    {
        MemorySearchScoring.Decide(C(0, -0.05, 0.05, p: 1), C(0, -0.05, 0.05, p: 1), C(0, -0.04, 0.04, p: 1)).Should().Be(Verdict.NoBetter);
    }

    private static MemorySearchEval.Options Options(string split = "test", int repetitions = 3, int? limit = null) =>
        new("hybrid", split, "claude-sonnet-5-5", "medium", repetitions, limit, 1, null);

    private static List<MemorySearchEval.TaskResult> KeepResults(int mTasks, int hTasks, int repetitions)
    {
        List<MemorySearchEval.TaskResult> results = [];
        for (int i = 0; i < mTasks; i++)
            results.Add(Result(Task() with { Id = $"m-{i}" }, off: 0, on: 1, repetitions));
        for (int i = 0; i < hTasks; i++)
            results.Add(Result(Task("control") with { Id = $"h-{i}" }, off: 1, on: 1, repetitions));
        return results;
    }

    [Fact]
    public void Report_RegisteredKeepRun_PassesGateAndListsExamples()
    {
        var report = MemorySearchEval.Report(Options(), KeepResults(64, 48, 3));

        ((bool)report["gate"]!["conforming"]!).Should().BeTrue();
        ((bool)report["gate"]!["passed"]!).Should().BeTrue();
        ((string)report["gate"]!["verdict"]!).Should().Be("KEEP");
        report["examples"]!["memoryHelped"]!.AsArray().Should().NotBeEmpty();
        report["examples"]!["memoryHurt"]!.AsArray().Should().BeEmpty();
        ((double)report["tokens"]!["on"]!["costUsd"]!).Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("dev", 3, null, 64, 48)]
    [InlineData("test", 1, null, 64, 48)]
    [InlineData("test", 3, 12, 6, 6)]
    [InlineData("test", 3, null, 60, 48)]
    public void Report_KeepOnAPartialRun_IsDiagnosticOnlyAndDoesNotPass(string split, int repetitions, int? limit, int mTasks, int hTasks)
    {
        var report = MemorySearchEval.Report(Options(split, repetitions, limit), KeepResults(mTasks, hTasks, repetitions));

        ((string)report["gate"]!["verdict"]!).Should().Be("KEEP");
        ((bool)report["gate"]!["conforming"]!).Should().BeFalse();
        report["gate"]!["diagnosticOnly"]!.AsArray().Should().NotBeEmpty();
        ((bool)report["gate"]!["passed"]!).Should().BeFalse();
    }

    [Fact]
    public void Nonconforming_TaskMissingARun_IsReported()
    {
        List<MemorySearchEval.TaskResult> results = KeepResults(64, 48, 3);
        results[0] = results[0] with { On = [.. results[0].On.Take(2)] };

        MemorySearchEval.Nonconforming(Options(), results).Should().ContainSingle().Which.Should().Contain("missing runs");
    }

    [Fact]
    public void RequireCompleteIndex_FailedDocument_StopsTheRun()
    {
        IndexReport failed = new(364, 1, ["ms-0042"]);

        FluentActions.Invoking(() => MemorySearchEval.RequireCompleteIndex(failed, 364, 364))
            .Should().Throw<InvalidOperationException>().WithMessage("*ms-0042*");
        FluentActions.Invoking(() => MemorySearchEval.RequireCompleteIndex(new IndexReport(364, 0, []), 364, 363))
            .Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => MemorySearchEval.RequireCompleteIndex(new IndexReport(364, 0, []), 364, 364))
            .Should().NotThrow();
    }

    private static MemorySearchEval.TaskResult Result(SearchTask task, double off, double on, int repetitions = 1)
    {
        (AgentRun, RunScore) Arm(double score) => (
            Run(AgentOutcome.Answered, "a", []),
            new RunScore(score, false, score > 0, score > 0, score, 1, 2, 0, 100, 10, 50));
        return new MemorySearchEval.TaskResult(task,
            [.. Enumerable.Repeat(Arm(off), repetitions)], [.. Enumerable.Repeat(Arm(on), repetitions)]);
    }

    [Fact]
    public void Compare_NinetyPercentLevel_GivesANarrowerIntervalThanNinetyFive()
    {
        double[] off = [0, 1, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0];
        double[] on = [1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 1];

        PairedComparison wide = PairedStats.Compare(off, on);
        PairedComparison narrow = PairedStats.Compare(off, on, confidenceLevel: 0.90);

        narrow.MeanDifference.Should().Be(wide.MeanDifference);
        (narrow.CiHigh - narrow.CiLow).Should().BeLessThan(wide.CiHigh - wide.CiLow);
    }

    [Fact]
    public void SystemPrompt_NamesTheContainerAndNeverMemory()
    {
        string prompt = MemorySearchEval.SystemPrompt(Guid.Parse(GoldUuid));

        prompt.Should().Contain(GoldUuid);
        prompt.Should().NotContainEquivalentOf("memory");
    }

    [Fact]
    public void Dataset_LoadsWithEveryCitationResolvable()
    {
        string root = Path.Combine(RepoRoot(), "eval", "datasets", "memory-search");
        List<SearchTask> tasks = MemorySearchEval.ReadTasks(Path.Combine(root, "tasks.jsonl"));
        var corpus = MemorySearchEval.ReadCorpus(Path.Combine(root, "corpus.jsonl"));
        List<MemorySearchEval.MemoryOp> ops = MemorySearchEval.ReadMemory(Path.Combine(root, "memory.jsonl"));
        HashSet<string> ids = [.. corpus.Select(d => d.Id)];

        tasks.Count(t => t.Split == "test").Should().Be(112);
        tasks.Where(t => t.Split == "test").Count(t => t.InMemorySet).Should().Be(64);
        corpus.Select(d => d.FileName).Should().OnlyHaveUniqueItems();
        tasks.SelectMany(t => t.Gold.Concat(t.Stale)).Should().OnlyContain(id => ids.Contains(id));
        ops.SelectMany(o => o.Sources).Should().OnlyContain(id => ids.Contains(id));
        ops.Where(o => o.Op == "correct").Should().OnlyContain(o => ops.Any(t => t.Id == o.Target));
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "eval", "MANIFEST.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }
}
