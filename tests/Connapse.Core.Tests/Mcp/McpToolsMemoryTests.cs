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
}
