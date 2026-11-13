using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Eval.Runs;
using Connapse.Eval.Runs.MemorySearch;
using FluentAssertions;

namespace Connapse.Eval.Tests.Runs;

[Trait("Category", "Unit")]
public class ToolRoutingTests
{
    private static Dictionary<string, JsonElement> Input(object o) =>
        JsonSerializer.SerializeToElement(o).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static RoutingTask Task(string gold = "delete_file", Dictionary<string, string>? expect = null) =>
        new("t01-" + gold, "test", "document", gold, "Delete notes-{u}.md from the docs container.",
            [("upload_file", new JsonObject { ["fileName"] = "notes-{u}.md", ["textContent"] = "x" })],
            expect ?? new Dictionary<string, string> { ["fileId|path|fileName"] = "notes-{u}" });

    private static AgentRun Run(params ToolTrace[] calls) =>
        new(AgentOutcome.Answered, "done", [], calls, TokenUsage.Zero, 1, null);

    [Fact]
    public void For_ReplacesThePlaceholderEverywhere()
    {
        RoutingTask t = Task().For("abc12345");

        t.Prompt.Should().Be("Delete notes-abc12345.md from the docs container.");
        ((string)t.Setup[0].Arguments["fileName"]!).Should().Be("notes-abc12345.md");
        t.Expect["fileId|path|fileName"].Should().Be("notes-abc12345");
        Task().Prompt.Should().Contain("{u}", "the template is not changed");
    }

    [Fact]
    public void Succeeded_DirectCallWithExpectedArgument_True()
    {
        RoutingTask t = Task().For("u1");
        RoutingScoring.Succeeded(t, [new ToolTrace("delete_file", Input(new { container = "docs", path = "/NOTES-U1.md" }), "Deleted")])
            .Should().BeTrue("any alternative key may hold the value, and case is ignored");
    }

    [Fact]
    public void Succeeded_ThroughCallDestructive_True()
    {
        RoutingTask t = Task().For("u1");
        ToolTrace call = new("call_destructive", Input(new { tool = "delete_file", arguments = new { fileName = "notes-u1.md" } }), "Deleted");

        RoutingScoring.Succeeded(t, [call]).Should().BeTrue();
    }

    [Fact]
    public void Succeeded_GoldToolFailed_False()
    {
        RoutingTask t = Task().For("u1");
        RoutingScoring.Succeeded(t, [new ToolTrace("delete_file", Input(new { path = "notes-u1.md" }), "Error: not found")]).Should().BeFalse();
        RoutingScoring.Succeeded(t, [new ToolTrace("delete_file", Input(new { path = "notes-u1.md" }), "ok", IsError: true)]).Should().BeFalse();
    }

    [Fact]
    public void Succeeded_CallToolRefusedForWrongRisk_False()
    {
        RoutingTask t = Task().For("u1");
        ToolTrace call = new("call_read", Input(new { tool = "delete_file", arguments = new { path = "notes-u1.md" } }),
            "Error: delete_file is destructive; run it with call_destructive.", IsError: true);

        RoutingScoring.Succeeded(t, [call]).Should().BeFalse();
    }

    [Fact]
    public void Succeeded_WrongToolOrArgument_False()
    {
        RoutingTask t = Task().For("u1");
        RoutingScoring.Succeeded(t, [new ToolTrace("bulk_delete", Input(new { paths = new[] { "notes-u1.md" } }), "Deleted")]).Should().BeFalse();
        RoutingScoring.Succeeded(t, [new ToolTrace("delete_file", Input(new { path = "notes-u2.md" }), "Deleted")]).Should().BeFalse();
        RoutingScoring.Succeeded(t, [new ToolTrace("delete_file", Input(new { container = "notes-u1" }), "Deleted")])
            .Should().BeFalse("the value must be under one of the expected keys");
    }

    [Fact]
    public void Succeeded_GoldCallPastTheCap_False()
    {
        RoutingTask t = Task().For("u1");
        List<ToolTrace> calls = [.. Enumerable.Range(0, AgentContract.MaxToolCalls).Select(_ => new ToolTrace("find_tools", Input(new { query = "x" }), "[]"))];
        calls.Add(new ToolTrace("delete_file", Input(new { path = "notes-u1.md" }), "Deleted"));

        RoutingScoring.Succeeded(t, calls).Should().BeFalse();
    }

    [Fact]
    public void Matches_NonStringArgument_ReadsItsJsonText()
    {
        Dictionary<string, string> expect = new() { ["paths"] = "b-u1.md" };
        RoutingScoring.Matches(expect, Input(new { paths = new[] { "a-u1.md", "b-u1.md" } })).Should().BeTrue();
        RoutingScoring.Matches(new Dictionary<string, string>(), Input(new { })).Should().BeTrue("no expectations means any arguments");
    }

    [Fact]
    public void ReadTasks_ReadsTheGeneratedFormat()
    {
        string path = Path.Combine(Path.GetTempPath(), $"routing-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path,
            """{"expect":{"name":"proj-{u}"},"gold":"container_create","group":"document","id":"t01-container_create","prompt":"Make proj-{u}.","setup":[],"split":"test"}""" + "\n");
        try
        {
            RoutingTask t = ToolRoutingEval.ReadTasks(path).Single();
            t.Gold.Should().Be("container_create");
            t.Split.Should().Be("test");
            t.Expect["name"].Should().Be("proj-{u}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadTasks_TheCommittedDatasetHasTheRegisteredTestSplit()
    {
        string root = RepoRoot();
        List<RoutingTask> tasks = ToolRoutingEval.ReadTasks(Path.Combine(root, "eval", "datasets", "tool-routing", "tasks.jsonl"));
        List<RoutingTask> test = [.. tasks.Where(t => t.Split == "test")];

        test.Should().HaveCount(ToolRoutingEval.RegisteredTasks);
        test.GroupBy(t => t.Group).Should().AllSatisfy(g => g.Should().HaveCount(20));
        tasks.Select(t => t.Id).Should().OnlyHaveUniqueItems();
        tasks.Should().AllSatisfy(t => t.Prompt.Should().NotContain(t.Gold, "a prompt never names its tool"));

        ToolRoutingEval.Fixtures f = ToolRoutingEval.ReadFixtures(Path.Combine(root, "eval", "datasets", "tool-routing", "fixtures.json"));
        f.DocsContainer.Should().StartWith("eval-");
        f.CodeContainer.Should().StartWith("eval-");
        f.Documents.Should().NotBeEmpty();
        f.Code.Should().NotBeEmpty();
        f.Facts.Should().NotBeEmpty();
    }

    [Fact]
    public void SystemPrompt_NamesThePlacesAndTheCapButNoTool()
    {
        ToolRoutingEval.Fixtures f = new("eval-docs", "eval-code", "space", [], [], []);
        string prompt = ToolRoutingEval.SystemPrompt(f);

        prompt.Should().Contain("eval-docs").And.Contain("eval-code").And.Contain("space").And.Contain($"{AgentContract.MaxToolCalls}");
        prompt.Should().NotContain("find_tools").And.NotContain("call_");
    }

    private static JsonObject Report(string list, bool search, double[] scores, double tokens, bool conforming = true) => new()
    {
        ["toolList"] = list,
        ["toolSearch"] = search,
        ["conforming"] = conforming,
        ["toolUse"] = JsonNode.Parse($"{{\"medianInputTokens\":{tokens}}}"),
        ["perTask"] = JsonNode.Parse(JsonSerializer.Serialize(scores.Select((s, i) => (s, i)).ToDictionary(x => $"t{x.i:00}", x => x.s))),
    };

    [Fact]
    public void ToolArms_EqualArms_NonInferiorAndTokenRatio()
    {
        double[] scores = [1, 0, 1, 1, 0.6667, 1, 0, 1, 1, 0.3333];
        JsonObject result = ToolArms.Compare(Report("full", false, scores, 20000), Report("compact", false, scores, 12000));

        ((bool)result["nonInferior"]!).Should().BeTrue();
        ((double)result["tokenRatio"]!).Should().Be(0.6);
        ((bool)result["tokenBarMet"]!).Should().BeTrue();
        result["diagnosticOnly"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public void ToolArms_CompactMuchWorse_NotNonInferior()
    {
        double[] full = [.. Enumerable.Repeat(1.0, 20)];
        double[] compact = [.. Enumerable.Range(0, 20).Select(i => i % 2 == 0 ? 1.0 : 0.0)];
        JsonObject result = ToolArms.Compare(Report("full", true, full, 20000), Report("compact", true, compact, 19000, conforming: false));

        ((bool)result["nonInferior"]!).Should().BeFalse();
        result["tokenBarMet"].Should().BeNull("bar 2 is judged with tool search off only");
        result["diagnosticOnly"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public void ToolArms_MismatchedReports_Throw()
    {
        double[] s = [1, 0, 1];
        FluentActions.Invoking(() => ToolArms.Compare(Report("compact", false, s, 1), Report("full", false, s, 1)))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => ToolArms.Compare(Report("full", false, s, 1), Report("compact", true, s, 1)))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => ToolArms.Compare(Report("full", false, s, 1), Report("compact", false, [1, 0], 1)))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Nonconforming_DevSplitAndMissingToolSearch_Listed()
    {
        ToolRoutingEval.Options options = new("compact", null, "dev", MemorySearchEval.RegisteredModel, MemorySearchEval.RegisteredEffort,
            MemorySearchEval.RegisteredRepetitions, null, 1, null);

        List<string> reasons = ToolRoutingEval.Nonconforming(options, []);

        reasons.Should().Contain(r => r.Contains("split")).And.Contain(r => r.Contains("--tool-search")).And.Contain(r => r.Contains("tasks"));
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "eval", "MANIFEST.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
