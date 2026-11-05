using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    private const string InitLine = """{"type":"system","subtype":"init","tools":["mcp__connapse__search_knowledge","mcp__connapse__answer"]}""";

    private const string AllowedLine =
        """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","unifiedWindows":{"seven_day":{"utilization":0.31},"five_hour":{"utilization":0.05}}}}""";

    private const string RejectedLine =
        """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","unifiedWindows":{"five_hour":{"utilization":1.0}}}}""";

    private const string ResultLine =
        """{"type":"result","subtype":"success","is_error":false,"stop_reason":"end_turn","num_turns":3,"result":"done","modelUsage":{"claude-sonnet-5-5":{"inputTokens":100,"outputTokens":20,"cacheReadInputTokens":300,"cacheCreationInputTokens":40,"costUSD":0.01},"claude-haiku-5-5":{"inputTokens":5,"outputTokens":1,"cacheReadInputTokens":0,"cacheCreationInputTokens":0,"costUSD":0.001}}}""";

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

    private sealed class FakeRunner(Func<ProcessStartInfo, Task<ProcessResult>> run) : IProcessRunner
    {
        public ProcessStartInfo? Started { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken ct)
        {
            Started = start;
            return run(start);
        }
    }

    private static ProcessResult Printed(params string[] lines) => new(0, lines, "", TimedOut: false);

    private static JsonObject Rpc(string method, object? parameters = null, int? id = 1)
    {
        JsonObject message = new() { ["jsonrpc"] = "2.0", ["method"] = method };
        if (id is not null)
            message["id"] = id;
        if (parameters is not null)
            message["params"] = JsonSerializer.SerializeToNode(parameters);
        return message;
    }

    private static string McpUrl(ProcessStartInfo start) =>
        JsonNode.Parse(start.ArgumentList[start.ArgumentList.IndexOf("--mcp-config") + 1])!["mcpServers"]!["connapse"]!["url"]!.GetValue<string>();

    [Fact]
    public async Task CallAsync_SearchThenAnswer_RecordsTraceAnswerAndCitations()
    {
        BridgeSession session = new(new FakeTools("found it"));

        (string text, bool isError) = await session.CallAsync("search_knowledge", Input(new { query = "q" }), CancellationToken.None);
        await session.CallAsync("answer", Input(new { answer = "270 days", documentIds = new[] { GoldUuid } }), CancellationToken.None);

        (text, isError).Should().Be(("found it", false));
        session.Outcome.Should().Be(AgentOutcome.Answered);
        session.Answer.Should().Be("270 days");
        session.Cited.Should().Equal(GoldUuid);
        session.Calls.Should().ContainSingle().Which.Name.Should().Be("search_knowledge");
        session.Tools.Select(t => t.Name).Should().Equal("search_knowledge", "answer");
    }

    [Fact]
    public async Task CallAsync_PastTwelveCalls_IsOutOfCallsAndDoesNotRunTheTool()
    {
        FakeTools tools = new();
        BridgeSession session = new(tools);

        for (int i = 0; i <= AgentContract.MaxToolCalls; i++)
            await session.CallAsync("search_knowledge", Input(new { }), CancellationToken.None);

        session.Outcome.Should().Be(AgentOutcome.OutOfCalls);
        tools.Calls.Should().Be(AgentContract.MaxToolCalls);
    }

    [Fact]
    public async Task CallAsync_ToolThrows_IsErrorWithReason()
    {
        BridgeSession session = new(new FakeTools());

        (_, bool isError) = await session.CallAsync("boom", Input(new { }), CancellationToken.None);

        isError.Should().BeTrue();
        session.Outcome.Should().Be(AgentOutcome.Error);
        session.Error.Should().Contain("tool broke");
    }

    [Fact]
    public async Task CallAsync_AfterTheRunCloses_IsRefusedAndNotRecorded()
    {
        FakeTools tools = new();
        BridgeSession session = new(tools);
        await session.CallAsync("answer", Input(new { answer = "x", documentIds = new[] { GoldUuid } }), CancellationToken.None);

        (string text, bool isError) = await session.CallAsync("search_knowledge", Input(new { }), CancellationToken.None);
        await session.CallAsync("answer", Input(new { answer = "y", documentIds = new[] { StaleUuid } }), CancellationToken.None);

        (text, isError).Should().Be((BridgeSession.TaskOver, true));
        tools.Calls.Should().Be(0);
        session.Answer.Should().Be("x");
        session.Cited.Should().Equal(GoldUuid);
    }

    [Fact]
    public async Task HandleAsync_InitializeListAndCall_SpeakMcp()
    {
        await using ToolBridge bridge = new();
        (string id, _) = bridge.Open(new FakeTools("found it"));

        JsonObject init = (await bridge.HandleAsync(id, Rpc("initialize", new { protocolVersion = "2025-06-18" }), CancellationToken.None))!;
        JsonObject list = (await bridge.HandleAsync(id, Rpc("tools/list"), CancellationToken.None))!;
        JsonObject call = (await bridge.HandleAsync(id, Rpc("tools/call", new { name = "search_knowledge", arguments = new { query = "q" } }), CancellationToken.None))!;

        init["result"]!["protocolVersion"]!.GetValue<string>().Should().Be("2025-06-18");
        init["result"]!["capabilities"]!["tools"].Should().NotBeNull();
        list["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).Should().Equal("search_knowledge", "answer");
        list["result"]!["tools"]![1]!["inputSchema"]!["required"]!.AsArray().Select(r => r!.GetValue<string>()).Should().Equal("answer", "documentIds");
        call["result"]!["content"]![0]!["text"]!.GetValue<string>().Should().Be("found it");
        call["result"]!["isError"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_NotificationUnknownMethodAndUnknownSession_AnswerAsMcpExpects()
    {
        await using ToolBridge bridge = new();
        (string id, _) = bridge.Open(new FakeTools());

        JsonObject? notification = await bridge.HandleAsync(id, Rpc("notifications/initialized", id: null), CancellationToken.None);
        JsonObject unknown = (await bridge.HandleAsync(id, Rpc("resources/list"), CancellationToken.None))!;
        bridge.Close(id);

        notification.Should().BeNull();
        unknown["error"]!["code"]!.GetValue<int>().Should().Be(-32601);
        await FluentActions.Awaiting(() => bridge.HandleAsync(id, Rpc("ping"), CancellationToken.None)).Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task StartAsync_ServesSessionsOverLoopbackHttp()
    {
        await using ToolBridge bridge = new();
        await bridge.StartAsync(CancellationToken.None);
        (string id, _) = bridge.Open(new FakeTools());
        using HttpClient http = new();

        HttpResponseMessage ping = await http.PostAsync(bridge.UrlFor(id), new StringContent(Rpc("ping").ToJsonString(), Encoding.UTF8, "application/json"));
        HttpResponseMessage missing = await http.PostAsync($"{bridge.BaseUrl}/mcp/nope", new StringContent(Rpc("ping").ToJsonString(), Encoding.UTF8, "application/json"));
        HttpResponseMessage get = await http.GetAsync(bridge.UrlFor(id));

        bridge.BaseUrl.Should().StartWith("http://127.0.0.1:");
        ping.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await ping.Content.ReadAsStringAsync())!["result"].Should().NotBeNull();
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        get.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public void Parse_StreamJson_ReadsToolsLimitAndUsageSummedOverModels()
    {
        CliResult result = CliResult.Parse(["not json", InitLine, AllowedLine, """{"type":"assistant"}""", ResultLine]);

        result.Finished.Should().BeTrue();
        result.IsError.Should().BeFalse();
        result.StopReason.Should().Be("end_turn");
        result.Turns.Should().Be(3);
        result.Tools.Should().Equal("mcp__connapse__search_knowledge", "mcp__connapse__answer");
        result.Usage.Should().BeEquivalentTo(new TokenUsage(105, 21, 300, 40, 0.011), o => o.Using<double>(c => c.Subject.Should().BeApproximately(c.Expectation, 1e-9)).WhenTypeIs<double>());
        result.Limit!.Allowed.Should().BeTrue();
        result.Limit.Utilization["seven_day"].Should().Be(0.31);
        result.Limit.ToString().Should().Be("allowed (five_hour 5%, seven_day 31%)");
    }

    [Fact]
    public async Task Compose_SessionOutcomeComesFirst()
    {
        BridgeSession session = new(new FakeTools());
        await session.CallAsync("answer", Input(new { answer = "x", documentIds = new[] { GoldUuid } }), CancellationToken.None);

        AgentRun run = ClaudeCliAgent.Compose(session, CliResult.None, new ProcessResult(1, [], "", TimedOut: true));

        run.Outcome.Should().Be(AgentOutcome.Answered);
        run.Cited.Should().Equal(GoldUuid);
    }

    [Theory]
    [InlineData("timeout", AgentOutcome.Error, "timed out")]
    [InlineData("no-result", AgentOutcome.Error, "without a result")]
    [InlineData("no-tools", AgentOutcome.Error, "did not get the eval's tools")]
    [InlineData("refusal", AgentOutcome.Refused, null)]
    [InlineData("is-error", AgentOutcome.Error, "error_max_turns")]
    [InlineData("stopped", AgentOutcome.NoAnswer, null)]
    public void Compose_WithoutAnAnswer_NamesHowTheSessionEnded(string shape, AgentOutcome expected, string? error)
    {
        CliResult finished = CliResult.Parse([InitLine, ResultLine]);
        (CliResult result, ProcessResult process) = shape switch
        {
            "timeout" => (CliResult.None, new ProcessResult(-1, [], "", TimedOut: true)),
            "no-result" => (CliResult.None, new ProcessResult(1, [], "boom", TimedOut: false)),
            "no-tools" => (finished with { Tools = ["Read"] }, Printed()),
            "refusal" => (finished with { StopReason = "refusal" }, Printed()),
            "is-error" => (finished with { IsError = true, Subtype = "error_max_turns" }, Printed()),
            _ => (finished, Printed()),
        };

        AgentRun run = ClaudeCliAgent.Compose(new BridgeSession(new FakeTools()), result, process);

        run.Outcome.Should().Be(expected);
        if (error is null)
            run.Error.Should().BeNull();
        else
            run.Error.Should().Contain(error);
        run.Usage.Should().Be(result.Usage);
    }

    [Fact]
    public void StartInfo_IsolatesTheAgentAndDropsApiKeys()
    {
        ClaudeCliAgent agent = new(new ToolBridge(), "claude-sonnet-5-5", "medium", "E:/agent");

        ProcessStartInfo start = agent.StartInfo("http://127.0.0.1:1/mcp/abc", "sys", "q?");

        start.WorkingDirectory.Should().Be("E:/agent");
        start.ArgumentList.Should().ContainInOrder("-p", "q?").And.ContainInOrder("--model", "claude-sonnet-5-5")
            .And.ContainInOrder("--system-prompt", "sys").And.ContainInOrder("--tools", "")
            .And.ContainInOrder("--allowedTools", "mcp__connapse").And.ContainInOrder("--setting-sources", "")
            .And.Contain(["--strict-mcp-config", "--restricted", "--disable-slash-commands", "--no-session-persistence"]);
        McpUrl(start).Should().Be("http://127.0.0.1:1/mcp/abc");
        start.Environment.Keys.Should().NotContain(ClaudeCliAgent.RemovedVariables);
    }

    [Fact]
    public async Task RunAsync_AgentAnswersThroughTheBridge_ReturnsTheAnswerAndUsage()
    {
        await using ToolBridge bridge = new();
        await bridge.StartAsync(CancellationToken.None);
        using HttpClient http = new();
        FakeRunner runner = new(async start =>
        {
            JsonObject call = Rpc("tools/call", new { name = "answer", arguments = new { answer = "270 days", documentIds = new[] { GoldUuid } } });
            await http.PostAsync(McpUrl(start), new StringContent(call.ToJsonString(), Encoding.UTF8, "application/json"));
            return Printed(InitLine, AllowedLine, ResultLine);
        });
        ClaudeCliAgent agent = new(bridge, "claude-sonnet-5-5", "medium", ".", runner);

        AgentRun run = await agent.RunAsync(new FakeTools(), "sys", "q?", CancellationToken.None);

        run.Outcome.Should().Be(AgentOutcome.Answered);
        run.Answer.Should().Be("270 days");
        run.Cited.Should().Equal(GoldUuid);
        run.Usage.Input.Should().Be(105);
        agent.FirstLimit!.Utilization["seven_day"].Should().Be(0.31);
        agent.LastLimit.Should().Be(agent.FirstLimit);
        string id = McpUrl(runner.Started!).Split('/')[^1];
        await FluentActions.Awaiting(() => bridge.HandleAsync(id, Rpc("ping"), CancellationToken.None)).Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task RunAsync_SubscriptionLimitReachedBeforeAnAnswer_StopsTheRun()
    {
        await using ToolBridge bridge = new();
        await bridge.StartAsync(CancellationToken.None);
        ClaudeCliAgent agent = new(bridge, "claude-sonnet-5-5", "medium", ".", new FakeRunner(_ => System.Threading.Tasks.Task.FromResult(Printed(InitLine, RejectedLine))));

        await FluentActions.Awaiting(() => agent.RunAsync(new FakeTools(), "sys", "q?", CancellationToken.None))
            .Should().ThrowAsync<SubscriptionLimitException>().Where(e => e.Limit.Status == "rejected");
        agent.LastLimit!.Allowed.Should().BeFalse();
    }

    [Fact]
    public void ReadAnswer_LoneStringId_CountsAsOneCitation()
    {
        (string? answer, List<string> cited) = AgentContract.ReadAnswer(Input(new { answer = "x", documentIds = GoldUuid }));

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
        new(outcome, answer, cited, calls, new TokenUsage(100, 10, 50, 0, 0.002), 2, null);

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
        ((double)report["tokens"]!["on"]!["apiListPriceUsd"]!).Should().BeGreaterThan(0);
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
