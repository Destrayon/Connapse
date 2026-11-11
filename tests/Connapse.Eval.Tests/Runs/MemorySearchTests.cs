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
    public async Task CallAsync_AnswerWithoutAStringAnswer_IsAnErrorAndLeavesTheRunOpen()
    {
        BridgeSession session = new(new FakeTools());

        (string text, bool isError) = await session.CallAsync("answer", Input(new { answer = 5, documentIds = new[] { GoldUuid } }), CancellationToken.None);

        isError.Should().BeTrue();
        text.Should().Contain("answer");
        session.Outcome.Should().BeNull();
        session.Cited.Should().BeEmpty();
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
    public void Parse_PartialAndOddEvents_FallBackToDefaults()
    {
        CliResult result = CliResult.Parse(
        [
            "[1,2]",
            """{"type":"system","subtype":"status","tools":["Read"]}""",
            """{"type":"system","subtype":"init"}""",
            """{"type":"rate_limit_event"}""",
            """{"type":"rate_limit_event","rate_limit_info":{"unifiedWindows":{"seven_day":{"utilization":"high"},"five_hour":{},"weekly":null}}}""",
            """{"type":"result","modelUsage":{"m":5,"n":{"inputTokens":7}}}""",
        ]);

        result.Finished.Should().BeTrue();
        result.IsError.Should().BeFalse();
        result.Subtype.Should().BeNull();
        result.StopReason.Should().BeNull();
        result.Turns.Should().Be(0);
        result.Text.Should().BeNull();
        result.Tools.Should().BeEmpty();
        result.Usage.Should().Be(new TokenUsage(7, 0, 0, 0));
        result.Limit!.Status.Should().Be("unknown");
        result.Limit.Utilization.Should().BeEmpty();
        result.Limit.Allowed.Should().BeFalse();
    }

    [Fact]
    public void Parse_NoEvents_IsNone()
    {
        CliResult.Parse([]).Should().Be(CliResult.None);
        CliResult.Parse([ResultLine, """{"type":"result","is_error":true,"subtype":"error_during_execution"}"""]).Usage.Should().Be(TokenUsage.Zero);
    }

    [Fact]
    public void Parse_LaterRateLimitEvent_ReplacesTheEarlierOne()
    {
        CliResult.Parse([AllowedLine, RejectedLine]).Limit!.Status.Should().Be("rejected");
        CliResult.Parse([RejectedLine, AllowedLine]).Limit!.Status.Should().Be("allowed");
    }

    [Theory]
    [InlineData("allowed", true)]
    [InlineData("allowed_warning", true)]
    [InlineData("rejected", false)]
    [InlineData("unknown", false)]
    [InlineData("not_allowed", false)]
    public void Allowed_OnlyAnAllowedStatus(string status, bool allowed) =>
        new SubscriptionLimit(status, new Dictionary<string, double>()).Allowed.Should().Be(allowed);

    [Fact]
    public void SubscriptionLimit_ToString_RoundsEachWindowToAWholePercentInKeyOrder() =>
        new SubscriptionLimit("allowed", new Dictionary<string, double> { ["seven_day"] = 0.316, ["five_hour"] = 0.004 })
            .ToString().Should().Be("allowed (five_hour 0%, seven_day 32%)");

    [Fact]
    public async Task ProcessRunner_ReadsEveryLineAndTheExitCode()
    {
        ProcessStartInfo start = new("dotnet");
        start.ArgumentList.Add("--version");

        ProcessResult result = await new ProcessRunner().RunAsync(start, TimeSpan.FromMinutes(1), CancellationToken.None);

        result.TimedOut.Should().BeFalse();
        result.ExitCode.Should().Be(0);
        result.Stdout.Should().ContainSingle().Which.Should().MatchRegex(@"^\d+\.\d+");
        start.RedirectStandardOutput.Should().BeTrue();
        start.UseShellExecute.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessRunner_PastItsTimeout_ReportsTimedOut()
    {
        // A process that outlives the timeout by far, so the timer always fires first.
        ProcessStartInfo start = OperatingSystem.IsWindows() ? new("ping") : new("sleep");
        foreach (string a in OperatingSystem.IsWindows() ? ["-n", "60", "127.0.0.1"] : (string[])["60"])
            start.ArgumentList.Add(a);

        ProcessResult result = await new ProcessRunner().RunAsync(start, TimeSpan.FromMilliseconds(500), CancellationToken.None);

        result.Should().Be(new ProcessResult(-1, result.Stdout, "", TimedOut: true));
    }

    [Fact]
    public async Task ProcessRunner_Cancelled_Throws()
    {
        ProcessStartInfo start = new("dotnet");
        start.ArgumentList.Add("--version");

        await FluentActions.Awaiting(() => new ProcessRunner().RunAsync(start, TimeSpan.FromMinutes(1), new CancellationToken(canceled: true)))
            .Should().ThrowAsync<OperationCanceledException>();
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
    [InlineData("refusal", AgentOutcome.Refused, "\"type\":\"result\"")]
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
            "refusal" => (finished with { StopReason = "refusal" }, Printed(ResultLine)),
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
    public void Compose_Refusal_KeepsWhatTheApiReturnedWithoutTheToolList()
    {
        CliResult refused = CliResult.Parse([InitLine, ResultLine]) with { StopReason = "refusal" };

        AgentRun run = ClaudeCliAgent.Compose(new BridgeSession(new FakeTools()), refused, Printed(InitLine, "", ResultLine));

        run.Outcome.Should().Be(AgentOutcome.Refused);
        run.Error.Should().Be(ResultLine);
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
    public async Task RunAsync_FinishedWithoutASubscriptionLimit_StopsTheRun()
    {
        await using ToolBridge bridge = new();
        await bridge.StartAsync(CancellationToken.None);
        ClaudeCliAgent agent = new(bridge, "claude-sonnet-5-5", "medium", ".", new FakeRunner(_ => System.Threading.Tasks.Task.FromResult(Printed(InitLine, ResultLine))));

        await FluentActions.Awaiting(() => agent.RunAsync(new FakeTools(), "sys", "q?", CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*not signed in with the subscription*");
    }

    [Fact]
    public void RequireSubscription_UnfinishedWithoutALimit_IsLeftToCompose()
    {
        FluentActions.Invoking(() => ClaudeCliAgent.RequireSubscription(CliResult.None)).Should().NotThrow();
    }

    [Fact]
    public void RequireSubscription_UnfinishedAfterToolCallsWithoutALimit_Throws()
    {
        FluentActions.Invoking(() => ClaudeCliAgent.RequireSubscription(CliResult.None, usedTools: true))
            .Should().Throw<InvalidOperationException>().WithMessage("*not signed in with the subscription*");
    }

    [Fact]
    public void RequireNoProjectMemory_ClaudeMdInTheAgentFolder_Throws()
    {
        string dir = Path.Combine(Path.GetTempPath(), "connapse-eval-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            FluentActions.Invoking(() => MemorySearchEval.RequireNoProjectMemory(dir)).Should().NotThrow();
            File.WriteAllText(Path.Combine(dir, "CLAUDE.md"), "x");
            FluentActions.Invoking(() => MemorySearchEval.RequireNoProjectMemory(dir)).Should().Throw<InvalidOperationException>();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RemovedVariables_IncludeEveryProviderSelector()
    {
        ClaudeCliAgent.RemovedVariables.Should().Contain(["CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY", "ANTHROPIC_API_KEY"]);
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
    public void Describe_PinnedSearchParameters_AreNotOffered()
    {
        ToolSpec spec = McpToolbox.Describe(McpToolbox.Find("search_knowledge"), MemorySearchEval.Pinned["search_knowledge"]);

        spec.Properties.Keys.Should().BeEquivalentTo(["query", "containerId", "path", "minScore"]);
        spec.Required.Should().Equal("query", "containerId");
    }

    [Fact]
    public void Unpinned_AgentSendsPinnedArguments_DropsThemAndKeepsTheRest()
    {
        Dictionary<string, JsonElement> input = Input(new { query = "q", containerId = "c", mode = "Keyword", topK = 50 });

        IReadOnlyDictionary<string, JsonElement> kept = McpToolbox.Unpinned(input, MemorySearchEval.Pinned["search_knowledge"]);

        kept.Keys.Should().BeEquivalentTo(["query", "containerId"]);
        McpToolbox.Unpinned(input, []).Should().BeSameAs(input);
    }

    [Fact]
    public void Describe_MemoryRecall_HasNoRequiredArgument()
    {
        ToolSpec spec = McpToolbox.Describe(McpToolbox.Find("memory_recall"));

        spec.Required.Should().BeEmpty();
        spec.Properties["includeHistory"].GetProperty("type").GetString().Should().Be("boolean");
    }

    [Fact]
    public void Describe_TableDescribe_OffersTheTableIdAsAString()
    {
        ToolSpec spec = McpToolbox.Describe(McpToolbox.Find("table_describe"));

        spec.Properties.Keys.Should().BeEquivalentTo(["containerId", "tableId"]);
        spec.Required.Should().Equal("containerId", "tableId");
        spec.Properties["tableId"].GetProperty("type").GetString().Should().Be("string");
    }

    [Fact]
    public void Describe_TableAggregate_OffersTheRequestAsAnObject()
    {
        ToolSpec spec = McpToolbox.Describe(McpToolbox.Find("table_aggregate"));

        spec.Required.Should().Equal("containerId", "request");
        spec.Properties["request"].GetProperty("type").GetString().Should().Be("object");
    }

    [Fact]
    public void TryConvert_UuidString_GivesTheGuid()
    {
        Guid id = Guid.NewGuid();

        McpToolbox.TryConvert(JsonSerializer.SerializeToElement(id.ToString()), typeof(Guid), out object? value).Should().BeTrue();

        value.Should().Be(id);
    }

    [Fact]
    public void TryConvert_NotAUuid_Fails()
    {
        McpToolbox.TryConvert(JsonSerializer.SerializeToElement("orders"), typeof(Guid), out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"table":"t"}""")]
    [InlineData("\"{\\\"table\\\":\\\"t\\\"}\"")]
    public void TryConvert_JsonElementParameter_KeepsTheValueAsSent(string json)
    {
        JsonElement sent = JsonDocument.Parse(json).RootElement;

        McpToolbox.TryConvert(sent, typeof(JsonElement), out object? value).Should().BeTrue();

        ((JsonElement)value!).GetRawText().Should().Be(sent.GetRawText());
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

    private static MemorySearchEval.Options Options(string split = "test", int repetitions = 3, int? limit = null) =>
        new("hybrid", split, "claude-sonnet-5-5", "medium", repetitions, limit, 1, null);

    private static List<MemorySearchEval.TaskResult> Results(int mTasks, int hTasks, int repetitions)
    {
        List<MemorySearchEval.TaskResult> results = [];
        for (int i = 0; i < mTasks; i++)
            results.Add(Result(Task() with { Id = $"m-{i}" }, 0, repetitions));
        for (int i = 0; i < hTasks; i++)
            results.Add(Result(Task("control") with { Id = $"h-{i}" }, 1, repetitions));
        return results;
    }

    [Fact]
    public void Report_RegisteredRun_ConformsAndScoresEachSet()
    {
        var report = MemorySearchEval.Report(Options(), Results(64, 48, 3));

        ((bool)report["conforming"]!).Should().BeTrue();
        ((double)report["all"]!["score"]!).Should().BeApproximately(48.0 / 112, 0.0001);
        ((double)report["memorySet"]!["score"]!).Should().Be(0);
        ((double)report["holdoutSet"]!["score"]!).Should().Be(1);
        ((double)report["tokens"]!["apiListPriceUsd"]!).Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("dev", 3, null, 64, 48)]
    [InlineData("test", 1, null, 64, 48)]
    [InlineData("test", 3, 12, 6, 6)]
    [InlineData("test", 3, null, 60, 48)]
    public void Report_PartialRun_IsDiagnosticOnly(string split, int repetitions, int? limit, int mTasks, int hTasks)
    {
        var report = MemorySearchEval.Report(Options(split, repetitions, limit), Results(mTasks, hTasks, repetitions));

        ((bool)report["conforming"]!).Should().BeFalse();
        report["diagnosticOnly"]!.AsArray().Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("hybrid-rerank", "claude-sonnet-5-5", "medium", "config")]
    [InlineData("hybrid", "claude-opus-5-5", "medium", "model")]
    [InlineData("hybrid", "claude-sonnet-5-5", "high", "effort")]
    public void Report_UnregisteredSetting_IsDiagnosticOnly(string config, string model, string effort, string reason)
    {
        MemorySearchEval.Options options = Options() with { Config = config, Model = model, Effort = effort };

        var report = MemorySearchEval.Report(options, Results(64, 48, 3));

        ((bool)report["conforming"]!).Should().BeFalse();
        MemorySearchEval.Nonconforming(options, Results(64, 48, 3)).Should().ContainSingle().Which.Should().StartWith(reason);
    }

    [Fact]
    public void Nonconforming_TaskMissingARun_IsReported()
    {
        List<MemorySearchEval.TaskResult> results = Results(64, 48, 3);
        results[0] = results[0] with { Runs = [.. results[0].Runs.Take(2)] };

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

    private static MemorySearchEval.TaskResult Result(SearchTask task, double score, int repetitions = 1)
    {
        (AgentRun, RunScore) Scored() => (
            Run(AgentOutcome.Answered, "a", []),
            new RunScore(score, false, score > 0, score > 0, score, 1, 2, 100, 10, 50));
        return new MemorySearchEval.TaskResult(task, [.. Enumerable.Repeat(Scored(), repetitions)]);
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
        HashSet<string> ids = [.. corpus.Select(d => d.Id)];

        tasks.Count(t => t.Split == "test").Should().Be(112);
        tasks.Where(t => t.Split == "test").Count(t => t.InMemorySet).Should().Be(64);
        corpus.Select(d => d.FileName).Should().OnlyHaveUniqueItems();
        tasks.SelectMany(t => t.Gold.Concat(t.Stale)).Should().OnlyContain(id => ids.Contains(id));
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "eval", "MANIFEST.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }
}
