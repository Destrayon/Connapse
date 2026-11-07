using Connapse.Core;
using Connapse.Storage.Memory;
using Connapse.Web.Mcp;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Mcp;

[Trait("Category", "Unit")]
public class McpToolsMemoryTests
{
    [Fact]
    public void ParseDates_BareDateIsMidnightUtc_AndMissingIsNull()
    {
        var parsed = McpTools.ParseDates("2026-03-01", null);

        parsed.Should().NotBeNull();
        parsed!.Value.From.Should().Be(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        parsed.Value.To.Should().BeNull();
    }

    [Fact]
    public void ParseDates_NotADate_IsNull() =>
        McpTools.ParseDates("2026-03-01", "last tuesday").Should().BeNull();

    [Fact]
    public void ParseSources_UuidIsADocument_NodePrefixIsANode_AnythingElseFails()
    {
        Guid document = Guid.NewGuid();

        McpTools.ParseSources([document.ToString().ToUpperInvariant(), "node:HAL-17"]).Should().Equal(
            new MemorySource(MemorySourceKind.Document, document.ToString()),
            new MemorySource(MemorySourceKind.Node, "HAL-17"));
        McpTools.ParseSources(["HAL-17"]).Should().BeNull();
        McpTools.ParseSources(null).Should().BeEmpty();
    }

    [Theory]
    [InlineData("What is Halcyon's database now?", "what | is | halcyon | s | database | now")]
    [InlineData("a & b | !c <-> (d:*)", "a | b | c | d")]
    [InlineData("  ?! ", null)]
    [InlineData(null, null)]
    public void TsQuery_KeepsOnlyWordsAndOrsThem(string? text, string? expected) =>
        PostgresMemoryStore.TsQuery(text).Should().Be(expected);

    [Fact]
    public void NamedSpaces_OneSpaceAndNoList_IsOneSpace()
    {
        McpTools.NamedSpaces("halcyon", null, null).Should().Equal("halcyon");
        McpTools.NamedSpaces(null, "halcyon", []).Should().Equal("halcyon");
        McpTools.NamedSpaces("halcyon", "older", null).Should().Equal("halcyon");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NamedSpaces_NoSpaceNamed_ReadsEverySpace(string? space)
    {
        McpTools.NamedSpaces(space, null, null).Should().BeEmpty();
        McpTools.NamedSpaces(space, space, []).Should().BeEmpty();
    }

    [Fact]
    public void NamedSpaces_AList_ReadsItWithTheOneSpaceFirst()
    {
        McpTools.NamedSpaces(null, null, ["a", "b"]).Should().Equal("a", "b");
        McpTools.NamedSpaces(" ", null, ["a"]).Should().Equal("a");
        McpTools.NamedSpaces("halcyon", null, ["a", "b"]).Should().Equal("halcyon", "a", "b");
    }

    [Fact]
    public void Containers_ListWins_ElseTheOneContainer()
    {
        Guid one = Guid.NewGuid(), a = Guid.NewGuid(), b = Guid.NewGuid();

        new MemoryQuery(one).Containers.Should().Equal(one);
        new MemoryQuery(one, ContainerIds: []).Containers.Should().Equal(one);
        new MemoryQuery(one, ContainerIds: [a, b]).Containers.Should().Equal(a, b);
    }

    private static PostgresMemoryStore.WordCount Count(string word, int matches, int facts) =>
        new() { Word = word, Matches = matches, Facts = facts };

    [Fact]
    public void Distinctive_TooFewWordsOrFacts_KeepsEveryWord()
    {
        PostgresMemoryStore.Distinctive(["ashgrove"], [Count("ashgrove", 20, 20)]).Should().Equal("ashgrove");
        PostgresMemoryStore.Distinctive(["ashgrove", "19"], []).Should().Equal("ashgrove", "19");
        PostgresMemoryStore.Distinctive(["ashgrove", "19"], [Count("ashgrove", 7, 7), Count("19", 1, 7)])
            .Should().Equal("ashgrove", "19");
    }

    [Fact]
    public void Distinctive_AWordInMoreThanHalfTheFacts_IsLeftOut()
    {
        PostgresMemoryStore.Distinctive(["ashgrove", "19", "change"],
                [Count("ashgrove", 20, 20), Count("19", 1, 20), Count("change", 10, 20)])
            .Should().Equal("19", "change");
    }

    [Fact]
    public void Distinctive_NoKeptWordMatches_KeepsEveryWord()
    {
        PostgresMemoryStore.Distinctive(["ashgrove", "what"], [Count("ashgrove", 20, 20), Count("what", 0, 20)])
            .Should().Equal("ashgrove", "what");
    }
}
