using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Eval.Runs;
using Connapse.Eval.Runs.MemorySearch;
using Connapse.Eval.Statistics;
using FluentAssertions;

namespace Connapse.Eval.Tests.Runs;

[Trait("Category", "Unit")]
public class MemoryCheckedArmsTests
{
    private static MemorySearchEval.MemoryOp Remember(string id) => new("remember", id, null, "s-" + id, "a-" + id, "v", null, [], null);

    private static MemorySearchEval.MemoryOp Correct(string id, string target) => new("correct", id, target, null, null, "v2", null, [], null);

    private sealed record Saved(string Op, string Subject, string? WhatIsNew);

    private static async Task<(MemorySearchEval.ReplayTally Tally, List<Saved> Saves, List<(string Op, Guid Target, string Subject)> Corrects)> Replay(
        IReadOnlyList<MemorySearchEval.MemoryOp> ops, HashSet<string> undocumented, Func<string, string?, bool> holds)
    {
        List<Saved> saves = [];
        List<(string, Guid, string)> corrects = [];
        MemorySearchEval.ReplayTally tally = await MemorySearchEval.ReplayThroughCheckAsync(ops, undocumented,
            (op, subject, _, whatIsNew) =>
            {
                saves.Add(new Saved(op.Id, subject, whatIsNew));
                return Task.FromResult<Guid?>(holds(op.Id, whatIsNew) ? null : Guid.NewGuid());
            },
            (op, target, subject, _) =>
            {
                corrects.Add((op.Id, target, subject));
                return Task.FromResult(Guid.NewGuid());
            });
        return (tally, saves, corrects);
    }

    [Fact]
    public async Task ReplayThroughCheck_HeldUndocumentedNote_SavesAgainWithAReason()
    {
        var (tally, saves, _) = await Replay([Remember("a")], ["a"], (_, whatIsNew) => whatIsNew is null);

        saves.Select(s => s.WhatIsNew).Should().Equal(null, MemorySearchEval.ReplayReason);
        tally.Should().Match<MemorySearchEval.ReplayTally>(t => t.Held == 1 && t.Retried == 1 && t.Dropped == 0 && t.Saves == 2);
    }

    [Fact]
    public async Task ReplayThroughCheck_HeldDocumentedNote_IsDroppedWithoutARetry()
    {
        var (tally, saves, _) = await Replay([Remember("a")], [], (_, _) => true);

        saves.Should().ContainSingle();
        tally.Should().Match<MemorySearchEval.ReplayTally>(t => t.Held == 1 && t.Retried == 0 && t.Dropped == 1);
    }

    [Fact]
    public async Task ReplayThroughCheck_CorrectOfDroppedNote_BecomesACheckedRememberUnderTheTargetsSubject()
    {
        var (tally, saves, corrects) = await Replay([Remember("a"), Correct("b", "a")], [], (id, _) => id == "a");

        saves.Should().HaveCount(2);
        saves[1].Should().Be(new Saved("b", "s-a", null));
        corrects.Should().BeEmpty();
        tally.Should().Match<MemorySearchEval.ReplayTally>(t => t.Rewritten == 1 && t.Dropped == 1);
    }

    [Fact]
    public async Task ReplayThroughCheck_CorrectOfADroppedRewrite_FollowsTheChain()
    {
        var (tally, saves, corrects) = await Replay([Remember("a"), Correct("b", "a"), Correct("c", "b")], [], (id, _) => id != "c");

        saves.Last().Should().Be(new Saved("c", "s-a", null));
        corrects.Should().BeEmpty();
        tally.Should().Match<MemorySearchEval.ReplayTally>(t => t.Rewritten == 2 && t.Dropped == 2);
    }

    [Fact]
    public async Task ReplayThroughCheck_CorrectOfStoredNote_GoesThroughCorrectUnchecked()
    {
        var (tally, saves, corrects) = await Replay([Remember("a"), Correct("b", "a")], [], (_, _) => false);

        saves.Should().ContainSingle();
        corrects.Should().ContainSingle().Which.Subject.Should().Be("s-a");
        tally.Should().Match<MemorySearchEval.ReplayTally>(t => t.Held == 0 && t.Rewritten == 0);
    }

    private static SearchTask Task1(string? staleAnswer = null) =>
        new("t1", "alias", "test", false, "q", ["ms-1"], ["ms-2"], "blue", staleAnswer);

    private static string Off(string task, int rep, double score, params string[] cited) => new JsonObject
    {
        ["task"] = task,
        ["arm"] = "off",
        ["rep"] = rep,
        ["outcome"] = "Answered",
        ["score"] = score,
        ["answer"] = "It is blue.",
        ["cited"] = new JsonArray([.. cited.Select(c => (JsonNode?)c)]),
        ["calls"] = new JsonArray(new JsonObject { ["tool"] = "search_knowledge", ["input"] = new JsonObject { ["query"] = "x" } }),
        ["error"] = null,
    }.ToJsonString();

    [Fact]
    public void ReadOffRuns_FileLines_KeepsScoresPairedByZeroBasedRepetition()
    {
        var runs = MemorySearchEval.ReadOffRuns([Off("t1", 1, 1, "ms-1"), Off("t1", 2, 0, "ms-2")], [Task1()], 2);

        runs[("t1", 0)].Score.Score.Should().Be(1);
        runs[("t1", 1)].Score.Should().Match<RunScore>(s => s.Score == 0 && s.CitedStale && s.Searches == 1 && s.ToolCalls == 1);
        runs[("t1", 0)].Run.Cited.Should().Equal("ms-1");
    }

    [Fact]
    public void ReadOffRuns_OnLinesAndOtherTasks_AreIgnored()
    {
        string on = Off("t1", 1, 0).Replace("\"off\"", "\"on\"");
        string other = Off("t9", 1, 0);

        MemorySearchEval.ReadOffRuns([on, other, Off("t1", 1, 1)], [Task1()], 1).Keys.Should().Equal(("t1", 0));
    }

    [Fact]
    public void ReadOffRuns_StaleAnswerNamed_IsNotAnswerRight()
    {
        var runs = MemorySearchEval.ReadOffRuns([Off("t1", 1, 1)], [Task1("blue")], 1);

        runs[("t1", 0)].Score.AnswerRight.Should().BeFalse();
    }

    [Fact]
    public void ReadOffRuns_MissingRepetition_Throws()
    {
        Action read = () => MemorySearchEval.ReadOffRuns([Off("t1", 1, 1)], [Task1()], 2);

        read.Should().Throw<InvalidOperationException>().WithMessage("*t1 repetition 2*");
    }

    private static PairedComparison Compared(double mean, double low) => new(40, mean, low, mean + 0.1, 0.01, 0.01, 1, 0.1);

    [Theory]
    [InlineData(0.30, 0.01, -0.09, true)]
    [InlineData(0.29, 0.20, -0.09, false)]
    [InlineData(0.40, 0.00, -0.09, false)]
    [InlineData(0.40, 0.20, -0.10, false)]
    public void MeetsCheckedBar_Planted_NeedsGainIntervalAndHoldout(double mean, double low, double holdoutLow, bool expected) =>
        MemorySearchEval.MeetsCheckedBar(false, Compared(mean, low), Compared(0, holdoutLow)).Should().Be(expected);

    [Theory]
    [InlineData(-0.09, -0.09, true)]
    [InlineData(-0.10, -0.09, false)]
    [InlineData(-0.09, -0.10, false)]
    public void MeetsCheckedBar_Realistic_NeedsBothIntervalsAboveMinusATenth(double low, double holdoutLow, bool expected) =>
        MemorySearchEval.MeetsCheckedBar(true, Compared(-0.05, low), Compared(0, holdoutLow)).Should().Be(expected);

    [Fact]
    public void LearnPrompt_CheckingSaves_NamesTheContainerAsTheSpace()
    {
        Guid container = Guid.NewGuid();

        MemoryLearnedEval.LearnPrompt(container, saveInContainer: true).Should().Contain($"using space \"{container}\"");
        MemoryLearnedEval.LearnPrompt(container).Should().NotContain("using space");
    }

    [Fact]
    public void UndocumentedFacts_TaskLines_TakesOnlyUndocumentedAliasCurrentAndLocationFacts()
    {
        string Line(string kind, bool? documented, string fact) => new JsonObject
        {
            ["kind"] = kind,
            ["documented"] = documented,
            ["facts"] = new JsonArray(fact),
        }.ToJsonString();

        MemoryNoveltyEval.UndocumentedFacts([Line("alias", false, "a"), Line("current", true, "b"), Line("location", false, "c"), Line("policy", false, "d"), Line("alias", null, "e")])
            .Should().BeEquivalentTo("a", "c");
    }

    [Fact]
    public void SaveCheck_HeldThenSavedAgain_CountsTheHoldAndKeepsTheReason()
    {
        Dictionary<string, JsonElement> reasoned = new() { ["whatIsNew"] = JsonSerializer.SerializeToElement("Only the team knows.") };
        AgentRun run = new(AgentOutcome.Answered, "x", [], [
            new ToolTrace("memory_remember", new Dictionary<string, JsonElement>(), "Error: Not saved. These passages look close"),
            new ToolTrace("memory_remember", reasoned, "{\"remembered\":{}}"),
        ], TokenUsage.Zero, 0, null);

        JsonObject check = MemoryLearnedEval.SaveCheck([run]);

        ((int)check["held"]!).Should().Be(1);
        ((int)check["stored"]!).Should().Be(1);
        ((int)check["runsWithAHeldSave"]!).Should().Be(1);
        check["reasons"]!.AsArray().Select(r => (string)r!).Should().Equal("Only the team knows.");
    }
}
