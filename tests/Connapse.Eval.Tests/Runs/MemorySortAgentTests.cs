using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Eval.Runs;
using Connapse.Eval.Runs.MemorySearch;
using FluentAssertions;

namespace Connapse.Eval.Tests.Runs;

[Trait("Category", "Unit")]
public class MemorySortAgentTests
{
    private const string Stored = "{\"remembered\":{\"subject\":\"s\"}}";
    private const string Held = "Error: Not saved. These passages look close: ops.md ...";

    private static Dictionary<string, JsonElement> Input(object o) =>
        JsonSerializer.SerializeToElement(o).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static ToolTrace Remember(string output, string? whatIsNew = null) =>
        new("memory_remember", whatIsNew is null ? Input(new { subject = "s" }) : Input(new { subject = "s", whatIsNew }), output);

    private static AgentRun Run(params ToolTrace[] calls) =>
        new(AgentOutcome.Answered, "saved", [], calls, new TokenUsage(100, 10, 50, 0, 0.002), 2, null);

    private static MemoryNoveltyEval.Note Note(string id, bool restates, string source = "planted") =>
        new(id, source, "test", restates, "s", "a", "v");

    private static MemorySortAgentEval.Options Registered(int? limit = null) =>
        new("hybrid", "claude-sonnet-5-5", "medium", limit, 1, null, null, 0.42);

    [Fact]
    public void Classify_HeldThenSavedWithReason_IsStoredWithReason()
    {
        MemorySortAgentEval.Sorted s = MemorySortAgentEval.Classify(Run(
            new ToolTrace("search_knowledge", Input(new { query = "q" }), "hits"), Remember(Held), Remember(Stored, " not in the docs ")));

        s.Should().Be(new MemorySortAgentEval.Sorted(true, true, true, true));
    }

    [Fact]
    public void Classify_HeldAndGivenUp_IsNotStored()
    {
        MemorySortAgentEval.Classify(Run(Remember(Held))).Should().Be(new MemorySortAgentEval.Sorted(true, true, false, false));
    }

    [Fact]
    public void Classify_StoredFirstTry_HasNoReason_AndAnEmptyReasonDoesNotCount()
    {
        MemorySortAgentEval.Classify(Run(Remember(Stored))).Should().Be(new MemorySortAgentEval.Sorted(true, false, true, false));
        MemorySortAgentEval.Classify(Run(Remember(Stored, "  "))).SavedWithReason.Should().BeFalse();
    }

    [Fact]
    public void Classify_NoRememberCall_IsNotAttempted()
    {
        MemorySortAgentEval.Classify(Run(new ToolTrace("search_knowledge", Input(new { query = "q" }), "hits")))
            .Should().Be(new MemorySortAgentEval.Sorted(false, false, false, false));
    }

    [Fact]
    public void Shares_CountsRestatingDroppedAndNewKept()
    {
        List<(MemoryNoveltyEval.Note, AgentRun)> runs =
        [
            (Note("r1", true), Run(Remember(Held))),
            (Note("r2", true), Run(Remember(Held), Remember(Stored, "why"))),
            (Note("r3", true), Run()),
            (Note("r4", true), Run(Remember(Held))),
            (Note("n1", false), Run(Remember(Held), Remember(Stored, "why"))),
            (Note("n2", false), Run(Remember(Held))),
        ];

        JsonObject shares = MemorySortAgentEval.Shares(runs);

        ((double)shares["restatingDropped"]!).Should().Be(0.75);
        ((double)shares["newKept"]!).Should().Be(0.5);
        ((int)shares["restating"]!["noAttempt"]!).Should().Be(1);
        ((int)shares["new"]!["storedWithReason"]!).Should().Be(1);
    }

    [Fact]
    public void Report_PassesOnlyForTheRegisteredRun()
    {
        List<(MemoryNoveltyEval.Note, AgentRun)> all = [.. Enumerable.Range(0, MemorySortAgentEval.RegisteredNotes)
            .Select(i => (Note($"n{i}", i % 2 == 0), Run(i % 2 == 0 ? Remember(Held) : Remember(Stored))))];

        ((bool)MemorySortAgentEval.Report(Registered(), all.Count, all, null)["gate"]!["passed"]!).Should().BeTrue();
        ((bool)MemorySortAgentEval.Report(Registered(5), all.Count, all, null)["gate"]!["passed"]!).Should().BeFalse("a limited run is a smoke test");
        ((bool)MemorySortAgentEval.Report(Registered() with { Model = "claude-haiku-5-5" }, all.Count, all, null)["gate"]!["passed"]!).Should().BeFalse();
        ((bool)MemorySortAgentEval.Report(Registered(), all.Count, all[..10], null)["gate"]!["passed"]!).Should().BeFalse("a partial run cannot pass");
        SubscriptionLimit stop = new("allowed", new Dictionary<string, double> { ["seven_day"] = 0.43 });
        ((bool)MemorySortAgentEval.Report(Registered(), all.Count, all, stop)["gate"]!["passed"]!).Should().BeFalse();
    }

    [Theory]
    [InlineData(0.41, false)]
    [InlineData(0.42, true)]
    [InlineData(0.5, true)]
    public void PastStopLine_StopsAtOrAboveTheLine(double used, bool stops)
    {
        SubscriptionLimit limit = new("allowed", new Dictionary<string, double> { ["seven_day"] = used });

        ClaudeCliAgent.PastStopLine(limit, 0.42).Should().Be(stops);
        ClaudeCliAgent.PastStopLine(limit, null).Should().BeFalse("no line means no stop");
    }

    [Fact]
    public void PastStopLine_NoSevenDayReading_DoesNotStop()
    {
        ClaudeCliAgent.PastStopLine(new SubscriptionLimit("allowed", new Dictionary<string, double>()), 0.42).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("0.42", 0.42)]
    [InlineData("1", 1.0)]
    public void StopLine_ParsesAValidLine(string? value, double? expected)
    {
        MemorySortAgentEval.StopLine(value).Should().Be(expected);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1.5")]
    [InlineData("abc")]
    public void StopLine_RejectsAnInvalidLine(string value)
    {
        FluentActions.Invoking(() => MemorySortAgentEval.StopLine(value)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Prompt_NamesTheSpace_AndTheQuestionCarriesTheNote()
    {
        Guid container = Guid.NewGuid();

        MemorySortAgentEval.SystemPrompt(container).Should().Contain($"space \"{container}\"").And.NotContain("documents say");
        MemorySortAgentEval.Question(Note("x", true)).Should().Be("The note to keep: subject \"s\", attribute \"a\", value \"v\".");
    }
}
