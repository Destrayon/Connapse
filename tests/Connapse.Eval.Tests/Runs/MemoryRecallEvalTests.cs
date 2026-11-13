using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Eval.Runs;
using Connapse.Eval.Runs.MemorySearch;
using FluentAssertions;

namespace Connapse.Eval.Tests.Runs;

/// <summary>CON-342: the proof's right-answer reading, text-read measure, arms and rules, as eval/pass-bars.md sets them.</summary>
[Trait("Category", "Unit")]
public class MemoryRecallEvalTests
{
    private static MemoryEval.Question Q(string kind, string answer) =>
        new("ASH-001", "ASH", "heldout", kind, "q", JsonNode.Parse(answer)!.AsObject(), ["ASH-2026-01-05"]);

    private static readonly string[] Databases = ["PostgreSQL 16", "MariaDB 11", "PostgreSQL 15"];

    [Theory]
    [InlineData("PostgreSQL 15", true)]
    [InlineData("It is PostgreSQL 15 (since April).", true)]
    [InlineData("PostgreSQL 15, before that MariaDB 11", false)]
    [InlineData("postgresql 15", false)]
    [InlineData("PostgreSQL", false)]
    public void Right_CurrentValue_NamesTheExpectedValueAndNoOther(string answer, bool right) =>
        MemoryRecallEval.Right(Q("current", """{"value":"PostgreSQL 15"}"""), answer, Databases).Should().Be(right);

    [Fact]
    public void Right_AValueWithinTheExpectedOne_IsNotAnother()
    {
        string[] caches = ["Redis 7", "Redis 7.2"];
        MemoryRecallEval.Right(Q("as_of", """{"value":"Redis 7.2"}"""), "Redis 7.2", caches).Should().BeTrue();
        MemoryRecallEval.Right(Q("as_of", """{"value":"Redis 7"}"""), "Redis 7.2", caches).Should().BeFalse();
    }

    [Theory]
    [InlineData("2026-04-24", true)]
    [InlineData("On 2026-04-24 (ticket ASH-26).", true)]
    [InlineData("2026-04-24, after 2026-02-28", false)]
    [InlineData("April 24, 2026", false)]
    public void Right_When_HasOnlyTheExpectedDate(string answer, bool right) =>
        MemoryRecallEval.Right(Q("when", """{"date":"2026-04-24"}"""), answer, []).Should().Be(right);

    [Theory]
    [InlineData("release v1.2.0 and rate limit 1000 requests/minute", true)]
    [InlineData("release v1.2.0", false)]
    public void Right_Changed_NamesEveryNewValue(string answer, bool right) =>
        MemoryRecallEval.Right(Q("changed", """{"changes":[{"to":"v1.2.0"},{"to":"1000 requests/minute"}]}"""), answer, []).Should().Be(right);

    [Fact]
    public void Measure_CountsEveryRecallResultsCharacters_OrderAndLimits_AndAnUnansweredRunIsWrong()
    {
        Dictionary<string, JsonElement> newest = new()
        {
            ["order"] = JsonSerializer.SerializeToElement("newest"),
            ["limit"] = JsonSerializer.SerializeToElement(3),
        };
        List<ToolTrace> calls =
        [
            new("memory_recall", newest, new string('x', 120)),
            new("memory_recall", new Dictionary<string, JsonElement>(), new string('y', 30)),
            new("answer", new Dictionary<string, JsonElement>(), ""),
        ];
        MemoryEval.Question q = Q("current", """{"value":"PostgreSQL 15"}""");

        MemoryRecallEval.RunMeasure answered = MemoryRecallEval.Measure(q,
            new AgentRun(AgentOutcome.Answered, "PostgreSQL 15", [], calls, TokenUsage.Zero, 3, null), Databases);
        answered.Should().BeEquivalentTo(new MemoryRecallEval.RunMeasure(true, 150, 2, 1, [3, 10]));

        MemoryRecallEval.Measure(q, new AgentRun(AgentOutcome.OutOfCalls, "PostgreSQL 15", [], calls, TokenUsage.Zero, 13, null), Databases)
            .Should().Match<MemoryRecallEval.RunMeasure>(m => !m.Right && m.TextRead == 150);
    }

    [Fact]
    public void Pinned_TodayHidesOrder_NewestOffersIt_BothHideContainerId()
    {
        MemoryRecallEval.Pinned("today")["memory_recall"].Should().BeEquivalentTo("containerId", "order");
        MemoryRecallEval.Pinned("newest")["memory_recall"].Should().BeEquivalentTo("containerId");
        FluentActions.Invoking(() => MemoryRecallEval.Pinned("oldest")).Should().Throw<ArgumentException>();

        ToolSpec today = McpToolbox.Describe(McpToolbox.Find("memory_recall"), MemoryRecallEval.Pinned("today")["memory_recall"]);
        today.Properties.Keys.Should().NotContain(["order", "containerId"]).And.Contain(["space", "limit"]);
    }

    [Fact]
    public void SystemPrompt_DiffersOnlyByTheNewestSentence()
    {
        string today = MemoryRecallEval.SystemPrompt("today");
        MemoryRecallEval.SystemPrompt("newest").Should().Be(today + " " + MemoryRecallEval.NewestSentence);
        today.Should().Contain("\"projects\"").And.NotContain("newest");
    }

    [Fact]
    public void Compare_ReadsLessWithNoFewerRight_PassesBothRules()
    {
        string[] tasks = [.. Enumerable.Range(1, 40).Select(i => $"T-{i:000}")];
        List<string> today = [.. tasks.SelectMany(t => Lines(t, right: 1, read: 900))];
        List<string> newest = [.. tasks.SelectMany((t, i) => Lines(t, right: 1, read: 300 + i))];

        JsonObject result = MemoryRecallEval.Compare(today, newest);

        ((string)result["r1TextRead"]!["verdict"]!).Should().Be("PASS");
        ((string)result["r2Right"]!["verdict"]!).Should().Be("PASS");
        ((string)result["verdict"]!).Should().Be("PASS");
    }

    [Fact]
    public void Compare_FewerRight_FailsR2()
    {
        string[] tasks = [.. Enumerable.Range(1, 40).Select(i => $"T-{i:000}")];
        List<string> today = [.. tasks.SelectMany(t => Lines(t, right: 1, read: 900))];
        List<string> newest = [.. tasks.SelectMany((t, i) => Lines(t, right: i % 3 == 0 ? 0 : 1, read: 300))];

        JsonObject result = MemoryRecallEval.Compare(today, newest);

        ((string)result["r2Right"]!["verdict"]!).Should().Be("FAIL");
        ((string)result["verdict"]!).Should().Be("FAIL");
    }

    [Fact]
    public void Compare_DifferentTasks_IsRefused() =>
        FluentActions.Invoking(() => MemoryRecallEval.Compare([.. Lines("T-1", 1, 10)], [.. Lines("T-2", 1, 10)]))
            .Should().Throw<ArgumentException>();

    private static IEnumerable<string> Lines(string task, int right, int read) =>
        Enumerable.Range(1, 2).Select(rep => new JsonObject { ["task"] = task, ["rep"] = rep, ["right"] = right, ["textRead"] = read }.ToJsonString());
}
