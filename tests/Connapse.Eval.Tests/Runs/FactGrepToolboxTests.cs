using System.Text.Json;
using Connapse.Core;
using Connapse.Eval.Runs;
using Connapse.Eval.Runs.MemorySearch;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Eval.Tests.Runs;

[Trait("Category", "Unit")]
public class FactGrepToolboxTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Space = Guid.NewGuid();

    private static MemoryFact Fact(string subject, string attribute, string value, int order, DateTimeOffset? validFrom = null,
        DateTimeOffset? validTo = null, bool superseded = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Space, subject, attribute, value, validFrom, validTo, Now.AddMinutes(order),
            superseded ? Now : null, null, null, new MemoryCaller(null, null, null), []);

    private static Dictionary<string, JsonElement> Input(object args) =>
        JsonSerializer.SerializeToElement(args).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);

    [Fact]
    public void Render_ListsLiveFactsOldestFirst_HidesCorrectedVersions_MarksEnded()
    {
        MemoryFact old = Fact("Harrow", "release branch", "release/2.3", 0, superseded: true);
        MemoryFact head = Fact("Harrow", "release branch", "release/2.4", 2, validFrom: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        MemoryFact ended = Fact("Lantern", "on-call", "Priya", 1, validTo: Now.AddDays(-3));

        List<string> lines = FactGrepToolbox.Render([head, old, ended], Now);

        lines.Should().Equal(
            FactGrepToolbox.Header,
            $"{ended.Id} | Lantern | on-call | Priya |  | ended 2026-11-06",
            $"{head.Id} | Harrow | release branch | release/2.4 | 2026-10-01 | current");
    }

    [Fact]
    public void Render_FlattensLineBreaksSoEachFactIsOneLine()
    {
        FactGrepToolbox.Render([Fact("Harrow", "notes", "first\nsecond", 0)], Now)[1].Should().Contain("first second");
    }

    [Fact]
    public void Grep_ReturnsNumberedMatches_CaseSensitiveUnlessAsked()
    {
        List<string> lines = [FactGrepToolbox.Header, "a | Harrow | db | Postgres | | current", "b | Lantern | db | Redis | | current"];

        FactGrepToolbox.GrepLines(lines, Input(new { pattern = "harrow" })).Should().Be("No lines match.");
        FactGrepToolbox.GrepLines(lines, Input(new { pattern = "harrow", ignoreCase = true })).Should().Be("2: a | Harrow | db | Postgres | | current");
        FactGrepToolbox.GrepLines(lines, Input(new { pattern = "Postgres|Redis" })).Should().Be(
            "2: a | Harrow | db | Postgres | | current\n3: b | Lantern | db | Redis | | current");
    }

    [Fact]
    public void Grep_CapsAt50LinesAndSaysHowManyMore()
    {
        List<string> lines = [FactGrepToolbox.Header, .. Enumerable.Range(1, 60).Select(i => $"id{i} | s | a | v | | current")];

        string output = FactGrepToolbox.GrepLines(lines, Input(new { pattern = "current" }));

        output.Split('\n').Should().HaveCount(FactGrepToolbox.MaxGrepLines + 1);
        output.Should().EndWith("(10 more matching lines not shown; narrow the pattern)");
    }

    [Fact]
    public void Grep_InvalidOrMissingPattern_ReturnsAnError()
    {
        FactGrepToolbox.GrepLines([FactGrepToolbox.Header], Input(new { pattern = "(" })).Should().StartWith("Error: Invalid pattern");
        FactGrepToolbox.GrepLines([FactGrepToolbox.Header], Input(new { })).Should().StartWith("Error: Missing");
    }

    [Fact]
    public void Read_ReturnsTheRangeWithLineNumbers()
    {
        List<string> lines = ["h", "one", "two", "three"];

        FactGrepToolbox.ReadLines(lines, Input(new { offset = 2, limit = 2 })).Should().Be("2: one\n3: two\n(lines 2-3 of 4)");
        FactGrepToolbox.ReadLines(lines, Input(new { })).Should().EndWith("(lines 1-4 of 4)");
        FactGrepToolbox.ReadLines(lines, Input(new { offset = 9 })).Should().Be("The file has 4 lines.");
        FactGrepToolbox.ReadLines(lines, Input(new { limit = 101 })).Should().StartWith("Error");
    }

    [Fact]
    public async Task CallAsync_RendersAfreshEachCall_AndPassesWritesThrough()
    {
        List<MemoryFact> store = [Fact("Harrow", "db", "Postgres", 0)];
        IToolbox writes = Substitute.For<IToolbox>();
        writes.Tools.Returns([new ToolSpec("memory_remember", "save", new Dictionary<string, JsonElement>(), [])]);
        writes.CallAsync("memory_remember", Arg.Any<IReadOnlyDictionary<string, JsonElement>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                store.Add(Fact("Lantern", "cache", "Redis", 1));
                return "saved";
            });
        FactGrepToolbox tools = new(_ => Task.FromResult<IReadOnlyList<MemoryFact>>([.. store]), writes);

        tools.Tools.Select(t => t.Name).Should().Equal("grep", "read", "memory_remember");
        (await tools.CallAsync("grep", Input(new { pattern = "Redis" }), CancellationToken.None)).Should().Be("No lines match.");
        (await tools.CallAsync("memory_remember", Input(new { }), CancellationToken.None)).Should().Be("saved");
        (await tools.CallAsync("grep", Input(new { pattern = "Redis" }), CancellationToken.None)).Should().StartWith("3: ");
        (await tools.CallAsync("memory_recall", Input(new { }), CancellationToken.None)).Should().StartWith("Error: Unknown tool");
    }

    [Fact]
    public void ScoreFind_CountsGrepAsARecall_AndSeesGoldInItsOutput()
    {
        Guid head = Guid.NewGuid();
        MemoryAgentEval.Seeded seed = new(Space, new Dictionary<string, Guid> { ["f001"] = head }, new Dictionary<string, Guid>(),
            new Dictionary<string, Guid> { ["f001"] = Guid.NewGuid() });
        AgentRun run = new(AgentOutcome.Answered, "release/2.4", [head.ToString()],
            [new ToolTrace("grep", Input(new { pattern = "release" }), $"2: {head} | Harrow | release branch | release/2.4 |  | current")],
            new TokenUsage(10, 5, 0, 0, 0.0123), 2, null);

        MemoryAgentEval.TaskScore score = MemoryAgentEval.ScoreFind(
            new MemoryAgentEval.AgentTask("find-x", "find", "reworded", "test", "q", ["f001"], null, []), run, seed);

        score.Score.Should().Be(1);
        score.Measures["sawGold"].Should().Be(1);
        score.Measures["firstRecallHit"].Should().Be(1);
        score.Measures["recallCalls"].Should().Be(1);
        score.Measures["costUsd"].Should().Be(0.0123);
    }

    [Fact]
    public void Prompts_DifferOnlyInHowMemoryIsRead()
    {
        MemoryAgentEval.FindPrompt().Should().Contain("Orrin Systems").And.Contain("read it with the memory tools");
        MemoryAgentEval.FindPrompt("Quorvane Logistics", "grep").Should().Contain("Quorvane Logistics").And.Contain("search with grep and read")
            .And.NotContain("Orrin");
        MemoryAgentEval.SavePrompt("Quorvane Logistics", "grep").Should().Contain("memory_remember (space \"pm-desk\")");
    }
}
