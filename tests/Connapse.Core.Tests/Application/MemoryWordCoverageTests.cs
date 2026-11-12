using Connapse.Application.Memory;
using Connapse.Core;
using Connapse.Web.Mcp;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Application;

// Whether the facts a search shows hold what was asked, and what memory_recall says when they don't (CON-337).
[Trait("Category", "Unit")]
public class MemoryWordCoverageTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid SpaceId = Guid.NewGuid();
    private static readonly Container Space = new(SpaceId.ToString(), "pm-desk", null, DateTime.UtcNow, DateTime.UtcNow,
        Kind: ContainerKind.Memory, Audience: MemoryScope.Team);

    [Fact]
    public void From_OneFactHoldsEveryWord_IsWhole()
    {
        MemoryWordCoverage coverage = MemoryWordCoverage.From([("hollis", false, A), ("owner", false, A), ("owner", false, B)]);

        coverage.OneHoldsAll.Should().BeTrue();
        coverage.Partial.Should().BeFalse();
        coverage.Missing.Should().BeEmpty();
    }

    [Fact]
    public void From_WordNoFactHolds_IsPartial_AndNamesIt()
    {
        MemoryWordCoverage coverage = MemoryWordCoverage.From([("hollis", false, A), ("backup", false, null), ("owner", false, A)]);

        coverage.Partial.Should().BeTrue();
        coverage.Missing.Should().Equal("backup");
        coverage.Words.Should().Equal("hollis", "backup", "owner");
    }

    [Fact]
    public void From_WordsHeldOnlyByDifferentFacts_IsPartial_WithNothingMissing()
    {
        MemoryWordCoverage coverage = MemoryWordCoverage.From([("copperline", false, A), ("reviewer", false, B)]);

        coverage.Partial.Should().BeTrue();
        coverage.Missing.Should().BeEmpty();
    }

    [Fact]
    public void From_StopWordsAreLeftOut()
    {
        MemoryWordCoverage coverage = MemoryWordCoverage.From([("who", true, null), ("the", true, null), ("hollis", false, A)]);

        coverage.Words.Should().Equal("hollis");
        coverage.Partial.Should().BeFalse();
    }

    [Fact]
    public void From_OnlyStopWords_IsNotPartial()
    {
        MemoryWordCoverage.From([("the", true, null)]).Partial.Should().BeFalse();
        MemoryWordCoverage.From([]).Partial.Should().BeFalse();
    }

    private static SpacedRecall Recall(MemoryWordCoverage? coverage)
    {
        MemoryFact fact = new(Guid.NewGuid(), Guid.NewGuid(), SpaceId, "Hollis", "owner", "Dana", null, null, DateTimeOffset.UtcNow,
            null, null, null, new MemoryCaller(Guid.NewGuid(), null, "a"), [], MemoryScope.Team);
        return new SpacedRecall([new RecalledFact(fact, [])], new Dictionary<Guid, Container> { [SpaceId] = Space }, 0,
            Coverage: coverage);
    }

    [Fact]
    public void PartialNote_MissingWord_NamesIt_AndSaysNotToCiteNearFacts()
    {
        string? note = McpTools.PartialNote(Recall(new MemoryWordCoverage(["hollis", "backup"], ["backup"], false)));

        note.Should().StartWith("Partial match: no fact above holds \"backup\".");
        note.Should().Contain("memory does not hold the answer").And.Contain("cite none of them");
    }

    [Fact]
    public void PartialNote_WordsSpreadAcrossFacts_SaysNoSingleFactHoldsAll()
    {
        string? note = McpTools.PartialNote(Recall(new MemoryWordCoverage(["copperline", "reviewer"], [], false)));

        note.Should().StartWith("Partial match: no single fact above holds all of \"copperline\", \"reviewer\".");
    }

    [Fact]
    public void PartialNote_WholeMatchOrNoCoverage_SaysNothing()
    {
        McpTools.PartialNote(Recall(new MemoryWordCoverage(["hollis"], [], true))).Should().BeNull();
        McpTools.PartialNote(Recall(null)).Should().BeNull();
    }
}
