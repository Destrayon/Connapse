using Connapse.Application.Memory;
using Connapse.Core;
using Connapse.Core.Utilities;
using Connapse.Storage.Memory;
using Connapse.Web.Mcp;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Application;

// The parts of a memory search that need no database (CON-322): word weights, partial words, trigram matches and the
// subjects an empty search names.
[Trait("Category", "Unit")]
public class MemorySearchRankingTests
{
    [Fact]
    public void Idf_ARareWord_WeighsMoreThanACommonOne() =>
        PostgresMemoryStore.Idf(100, 1).Should().BeGreaterThan(PostgresMemoryStore.Idf(100, 60));

    [Fact]
    public void Idf_AWordInEveryFact_StaysPositive() =>
        PostgresMemoryStore.Idf(100, 100).Should().BePositive();

    [Fact]
    public void Terms_EachKindOfWord_GetsItsOwnTerm()
    {
        PostgresMemoryStore.TermCount[] counts =
        [
            new() { Word = "halcyon", Matches = 3, PrefixMatches = 3, Facts = 50 },
            new() { Word = "postgres", Matches = 0, PrefixMatches = 2, Facts = 50 },
            new() { Word = "postgresql", Matches = 0, PrefixMatches = 0, Facts = 50 },
            new() { Word = "the", Stop = true, Facts = 50 },
            new() { Word = "db", Matches = 0, PrefixMatches = 4, Facts = 50 },
        ];

        List<PostgresMemoryStore.SearchTerm> terms = PostgresMemoryStore.Terms(
            ["halcyon", "postgres", "postgresql", "the", "db"], counts, out List<string> unmatched);

        terms.Select(t => t.Query).Should().Equal("halcyon", "postgres:*");
        terms[0].Weight.Should().Be(PostgresMemoryStore.Idf(50, 3));
        terms[1].Weight.Should().Be(PostgresMemoryStore.Idf(50, 2) * PostgresMemoryStore.PartialWeight);
        unmatched.Should().Equal(["postgresql"], "a short word is never tried as a prefix or by trigrams");
    }

    [Fact]
    public void Trigrams_AShorterSpelling_IsSimilar()
    {
        Trigrams.Similarity("postgresql", "postgres").Should().BeApproximately(8.0 / 12, 1e-9);
        Trigrams.Similarity("postgresql", "redis").Should().BeLessThan(PostgresMemoryStore.TrigramThreshold);
    }

    [Fact]
    public void Similar_TakesAtMostTwoAboveTheThreshold_MostSimilarFirst() =>
        PostgresMemoryStore.Similar("postgresql", ["postgres", "postgre", "redis", "postgresql", "post"])
            .Should().Equal("postgres", "postgre");

    [Fact]
    public void Nearest_AMisspelledSubject_NamesIt() =>
        RecallMemory.Nearest("halcyn deploy", [new MemorySubject("Wicket", 4), new MemorySubject("Halcyon", 2)])
            .Should().Equal("Halcyon");

    [Fact]
    public void Nearest_NothingClose_NamesTheSubjectsWithTheMostFacts() =>
        RecallMemory.Nearest("zebra", [new("Wicket", 4), new("Halcyon", 9), new("Ledger", 1), new("Atlas", 6)])
            .Should().Equal("Halcyon", "Atlas", "Wicket");

    [Fact]
    public void Nearest_NoWordsOrNoSubjects_NamesNone()
    {
        RecallMemory.Nearest("a ?", [new MemorySubject("Wicket", 4)]).Should().BeEmpty();
        RecallMemory.Nearest("halcyon", []).Should().BeEmpty();
    }

    [Fact]
    public void NearestNote_SaysNothingMatched_AndTheNextCall()
    {
        Container space = new(Guid.NewGuid().ToString(), "pm-desk", null, DateTime.UtcNow, DateTime.UtcNow, Kind: ContainerKind.Memory);
        SpacedRecall empty = new([], new Dictionary<Guid, Container> { [Guid.Parse(space.Id)] = space }, 0, Nearest: ["Halcyon", "Wicket"]);

        string? note = McpTools.NearestNote(empty);

        note.Should().StartWith("Nothing matched.").And.Contain("\"Halcyon\", \"Wicket\"")
            .And.Contain("space=\"pm-desk\" and subject=\"Halcyon\"");
        McpTools.NearestNote(empty with { Nearest = null }).Should().BeNull();
    }
}
