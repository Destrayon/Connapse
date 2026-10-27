using System.Text.Json.Nodes;
using Connapse.Core;
using Connapse.Eval.Runs;
using FluentAssertions;

namespace Connapse.Eval.Tests.Runs;

[Trait("Category", "Unit")]
public class MemoryEvalTests
{
    private static MemoryEval.Question Q(string kind, string text, string answer, params string[] evidence) =>
        new("Q-1", "HAL", "heldout", kind, text, JsonNode.Parse(answer)!.AsObject(), evidence);

    private static MemoryFact Fact(string value, string? from, string? reason = null, bool corrected = false) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Halcyon", "database", value,
        from is null ? null : DateTimeOffset.Parse(from + "T00:00:00Z"), null, DateTimeOffset.UtcNow,
        corrected ? DateTimeOffset.UtcNow : null, null, reason, new MemoryCaller(null, null, null), []);

    [Theory]
    [InlineData("on MariaDB 11 now", "MariaDB 11", 13)]
    [InlineData("MariaDB 110 now", "MariaDB 11", -1)]
    [InlineData("from MariaDB 11 to MariaDB 11", "MariaDB 11", 29)]
    [InlineData("v1.0.0-rc", "v1.0.0", -1)]
    public void Named_MatchesWholeTokensOnly_AndReturnsTheLastEnd(string text, string value, int expected) =>
        MemoryEval.Named(text, value).Should().Be(expected);

    [Fact]
    public void Recall_AWindow_NeedsEveryChangesOwnEvidence()
    {
        var q = Q("changed", "What changed in Halcyon's setup between 2026-01-01 and 2026-02-01?",
            """{"changes":[{"evidence":["HAL-a"]},{"evidence":["HAL-b","HAL-c"]}]}""", "HAL-a", "HAL-b", "HAL-c");

        MemoryEval.Recall(q, new HashSet<string> { "HAL-a", "HAL-c" }).Should().Be(1);
        MemoryEval.Recall(q, new HashSet<string> { "HAL-b", "HAL-c" }).Should().Be(0);
    }

    [Fact]
    public void AnswerFromMemory_Current_ReadsTheFirstFactNamingAValueOfTheFact()
    {
        var q = Q("current", "What is Halcyon's database now?", """{"value":"MySQL 8.0"}""", "HAL-x");
        string[] values = ["MariaDB 11", "MySQL 8.0"];

        MemoryEval.AnswerFromMemory(q, [Fact("ca-central-1", "2026-01-05"), Fact("MySQL 8.0", "2026-03-24")], values).Should().Be(1);
        MemoryEval.AnswerFromMemory(q, [Fact("MariaDB 11", "2026-01-05"), Fact("MySQL 8.0", "2026-03-24")], values).Should().Be(0);
        MemoryEval.AnswerFromMemory(q, [], values).Should().Be(0);
    }

    [Fact]
    public void AnswerFromMemory_WhenAndTicket_ReadTheDateAndTheReasonsTicket()
    {
        string[] values = ["MariaDB 11", "MySQL 8.0"];
        MemoryFact changed = Fact("MySQL 8.0", "2026-03-24", "HAL-19 done: moved off MariaDB");

        MemoryEval.AnswerFromMemory(Q("when", "When did Halcyon's database change to MySQL 8.0?", """{"date":"2026-03-24"}"""),
            [changed], values).Should().Be(1);
        MemoryEval.AnswerFromMemory(Q("ticket", "Which ticket changed Halcyon's database to MySQL 8.0?", """{"ticket":"HAL-19"}"""),
            [changed], values).Should().Be(1);
        MemoryEval.AnswerFromMemory(Q("ticket_effect", "What did HAL-19 change in Halcyon?", "{}"), [changed], values)
            .Should().BeNull("baseline.py reads no answer for this kind");
    }

    [Fact]
    public void QueryFor_AsksAsAnAgentWouldForEachKind()
    {
        Guid c = Guid.NewGuid();

        MemoryEval.QueryFor(Q("current", "What is Halcyon's database now?", "{}"), c).Should()
            .Match<MemoryQuery>(m => m.AsOf == null && !m.History && m.Text != null);
        MemoryEval.QueryFor(Q("as_of", "What was Halcyon's cache on 2026-04-05?", "{}"), c).AsOf.Should()
            .Be(new DateTimeOffset(2026, 4, 5, 0, 0, 0, TimeSpan.Zero));
        MemoryEval.QueryFor(Q("changed", "What changed in Halcyon's setup between 2026-01-17 and 2026-03-08?", "{}"), c).Should()
            .Match<MemoryQuery>(m => m.ChangedFrom == new DateTimeOffset(2026, 1, 17, 0, 0, 0, TimeSpan.Zero)
                && m.ChangedTo == new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.Zero) && m.Text == null);
        MemoryEval.QueryFor(Q("when", "When did Halcyon's CI system change to Buildkite?", "{}"), c).History.Should().BeTrue();
    }
}
