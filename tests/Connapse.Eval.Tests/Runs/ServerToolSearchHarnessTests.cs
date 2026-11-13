using System.Text.Json;
using Connapse.Eval.Runs;
using Connapse.Eval.Runs.MemorySearch;
using FluentAssertions;
using ModelContextProtocol.Protocol;

namespace Connapse.Eval.Tests.Runs;

[Trait("Category", "Unit")]
public class ServerToolSearchHarnessTests
{
    private static Dictionary<string, JsonElement> Input(object o) =>
        JsonSerializer.SerializeToElement(o).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static AgentRun Run(int toolSearch = 0, double seconds = 1, long input = 100, params ToolTrace[] calls) =>
        new(AgentOutcome.Answered, "a", [], calls, new TokenUsage(input, 5, 0, 0), 1, null, ToolSearchCalls: toolSearch, Seconds: seconds);

    [Fact]
    public void EffectiveCall_DirectCall_IsItself()
    {
        EffectiveCall c = EffectiveCall.Of(new ToolTrace("search_knowledge", Input(new { query = "x" }), "ok"));

        c.Tool.Should().Be("search_knowledge");
        c.ViaCallTool.Should().BeFalse();
        c.Arguments["query"].GetString().Should().Be("x");
    }

    [Fact]
    public void EffectiveCall_CallTool_UnwrapsToolAndArguments()
    {
        EffectiveCall c = EffectiveCall.Of(new ToolTrace("call_write", Input(new { tool = "upload_file", arguments = new { fileName = "a.md" } }), "ok"));

        c.Tool.Should().Be("upload_file");
        c.ViaCallTool.Should().BeTrue();
        c.Arguments["fileName"].GetString().Should().Be("a.md");
    }

    [Fact]
    public void EffectiveCall_ApiOperations_AreNamedByOperation()
    {
        EffectiveCall.Of(new ToolTrace("connapse_api_read", Input(new { operation = "settings.get" }), "ok")).Tool.Should().Be("api:settings.get");
        EffectiveCall.Of(new ToolTrace("call_read", Input(new { tool = "settings.get", arguments = new { } }), "ok")).Tool.Should().Be("api:settings.get");
    }

    [Fact]
    public void EffectiveCall_Failed_ByFlagOrErrorText()
    {
        EffectiveCall.Of(new ToolTrace("x", Input(new { }), "  Error: no")).Failed.Should().BeTrue();
        EffectiveCall.Of(new ToolTrace("x", Input(new { }), "fine", IsError: true)).Failed.Should().BeTrue();
        EffectiveCall.Of(new ToolTrace("x", Input(new { }), "fine")).Failed.Should().BeFalse();
    }

    [Fact]
    public void EffectiveCall_CallToolWithoutToolName_StaysTheCallTool()
    {
        EffectiveCall.Of(new ToolTrace("call_read", Input(new { arguments = new { } }), "Error")).Tool.Should().Be("call_read");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("full", "full")]
    [InlineData("compact", "compact")]
    public void ParseToolList_KnownValues(string? value, string? expected) =>
        MemorySearchEval.ParseToolList(value).Should().Be(expected);

    [Theory]
    [InlineData(null, null)]
    [InlineData("on", true)]
    [InlineData("off", false)]
    public void ParseToolSearch_KnownValues(string? value, bool? expected) =>
        MemorySearchEval.ParseToolSearch(value).Should().Be(expected);

    [Fact]
    public void Parse_UnknownValues_Throw()
    {
        FluentActions.Invoking(() => MemorySearchEval.ParseToolList("half")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => MemorySearchEval.ParseToolSearch("maybe")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void InvalidToolSearchArm_OnlyWhenOnAndNeverCalled()
    {
        MemorySearchEval.InvalidToolSearchArm(true, [Run(), Run()]).Should().BeTrue();
        MemorySearchEval.InvalidToolSearchArm(true, [Run(), Run(toolSearch: 1)]).Should().BeFalse();
        MemorySearchEval.InvalidToolSearchArm(false, [Run()]).Should().BeFalse();
        MemorySearchEval.InvalidToolSearchArm(null, [Run()]).Should().BeFalse();
    }

    [Fact]
    public void ToolMeasures_CountsFindToolsFailuresAndMedians()
    {
        AgentRun a = Run(1, 2, 100,
            new ToolTrace("find_tools", Input(new { query = "delete" }), "[]"),
            new ToolTrace("call_read", Input(new { tool = "delete_file", arguments = new { } }), "Error: destructive", IsError: true));
        AgentRun b = Run(0, 4, 300, new ToolTrace("delete_file", Input(new { }), "ok"));
        AgentRun c = Run(0, 9, 200);

        System.Text.Json.Nodes.JsonObject m = MemorySearchEval.ToolMeasures([a, b, c]);

        ((int)m["runs"]!).Should().Be(3);
        ((double)m["findToolsCallsPerRun"]!).Should().BeApproximately(1.0 / 3, 1e-3);
        ((double)m["failedCallsPerRun"]!).Should().BeApproximately(1.0 / 3, 1e-3);
        ((double)m["toolCallsPerRun"]!).Should().Be(1);
        ((double)m["medianInputTokens"]!).Should().Be(200);
        ((double)m["medianSeconds"]!).Should().Be(4);
    }

    [Fact]
    public void InputTokens_CountsCacheReadsAndWrites() =>
        MemorySearchEval.InputTokens(new TokenUsage(10, 99, 300, 40)).Should().Be(350);

    [Fact]
    public void PathOf_PicksTheList()
    {
        ServerToolbox.PathOf(true).Should().Be("mcp/full");
        ServerToolbox.PathOf(false).Should().Be("mcp");
    }

    [Fact]
    public void Spec_KeepsTheWireToolAndStripsPinnedParameters()
    {
        Tool tool = new()
        {
            Name = "search_knowledge",
            Description = "Search.",
            InputSchema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { query = new { type = "string" }, mode = new { type = "string" } },
                required = new[] { "query", "mode" },
            }),
            Annotations = new ToolAnnotations { ReadOnlyHint = true },
        };

        ToolSpec spec = ServerToolbox.Spec(tool, ["mode"]);

        spec.Properties.Keys.Should().Equal("query");
        spec.Required.Should().Equal("query");
        spec.Wire!["annotations"]!["readOnlyHint"]!.GetValue<bool>().Should().BeTrue("annotations reach the agent as the server sent them");
        spec.Wire["inputSchema"]!["properties"]!.AsObject().ContainsKey("mode").Should().BeFalse();
    }
}
