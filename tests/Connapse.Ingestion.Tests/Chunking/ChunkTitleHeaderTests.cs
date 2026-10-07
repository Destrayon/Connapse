using Connapse.Core.Interfaces;
using Connapse.Ingestion.Chunking;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Chunking;

[Trait("Category", "Unit")]
public class ChunkTitleHeaderTests
{
    private static ChunkInfo Chunk(string content, int index = 0) =>
        new(content, index, 10, 0, content.Length, new Dictionary<string, string>(), PrecomputedEmbedding: [1f, 0f]);

    [Fact]
    public void TitleOf_ParserTitle_WinsOverTheFileName()
    {
        ParsedDocument parsed = new("body", new Dictionary<string, string> { ["Title"] = "  Q3 Renewal  " }, []);

        ChunkTitleHeader.TitleOf(parsed, "0001.pdf").Should().Be("Q3 Renewal");
    }

    [Theory]
    [InlineData("Incident review - API gateway.txt", "Incident review - API gateway")]
    [InlineData(null, null)]
    [InlineData(".txt", null)]
    public void TitleOf_NoParserTitle_UsesTheFileNameWithoutItsExtension(string? fileName, string? expected)
    {
        ParsedDocument parsed = new("body", new Dictionary<string, string>(), []);

        ChunkTitleHeader.TitleOf(parsed, fileName).Should().Be(expected);
    }

    [Fact]
    public void Prepend_AddsTheTitleLineAndDropsThePooledVector()
    {
        IReadOnlyList<ChunkInfo> result = ChunkTitleHeader.Prepend([Chunk("Later part of the page.", 3)], "Runbook");

        result.Single().Content.Should().Be("Runbook\n\nLater part of the page.");
        result.Single().PrecomputedEmbedding.Should().BeNull("the embedding must include the title");
        result.Single().Metadata["OffsetEstimated"].Should().Be("true");
        result.Single().ChunkIndex.Should().Be(3);
    }

    [Fact]
    public void Prepend_ChunkAlreadyStartsWithTheTitle_IsLeftAlone()
    {
        ChunkInfo first = Chunk("Runbook\n\nFirst part.");

        ChunkTitleHeader.Prepend([first], "Runbook").Single().Should().BeSameAs(first);
    }

    [Fact]
    public void Prepend_NoTitle_ReturnsTheChunksUnchanged()
    {
        IReadOnlyList<ChunkInfo> chunks = [Chunk("Text.")];

        ChunkTitleHeader.Prepend(chunks, null).Should().BeSameAs(chunks);
    }
}
