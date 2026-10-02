using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Chunking;
using Connapse.Ingestion.Utilities;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Chunking;

/// <summary>
/// DocumentAware on the Markdown the PDF and Office parsers now write (#633): title-only slides,
/// long tables and the breadcrumb's share of the token budget.
/// </summary>
[Trait("Category", "Unit")]
public class DocumentAwareStructureTests
{
    private readonly TiktokenTokenCounter _counter = new();
    private readonly DocumentAwareChunker _chunker;

    public DocumentAwareStructureTests() => _chunker = new DocumentAwareChunker(_counter, new RecursiveChunker(_counter));

    private Task<IReadOnlyList<ChunkInfo>> ChunkAsync(string content, int maxChunkSize = 512) =>
        _chunker.ChunkAsync(
            new ParsedDocument(content, [], []),
            new ChunkingSettings { MaxChunkSize = maxChunkSize, MinChunkSize = 1, Overlap = 0, PrependHeaderPath = true });

    [Fact]
    public async Task ChunkAsync_TitleOnlySlide_StillBecomesAChunk()
    {
        var chunks = await ChunkAsync("## Slide 1: Quarterly results\n\nRevenue grew.\n\n## Slide 2: Questions?\n");

        chunks.Should().Contain(c => c.Content.Contains("Questions?"), "a title-only slide is still searchable text");
    }

    [Fact]
    public async Task ChunkAsync_DeckOfOnlyTitles_HasChunks()
    {
        var chunks = await ChunkAsync("## Slide 1: Welcome\n\n## Slide 2: Agenda\n");

        chunks.Should().HaveCount(2);
    }

    [Fact]
    public async Task ChunkAsync_HeadingFollowedByItsSubheading_AddsNoNoiseChunk()
    {
        var chunks = await ChunkAsync("# Report\n\n## Revenue\n\nIt grew.\n");

        chunks.Should().ContainSingle("the parent heading is carried by the child's breadcrumb");
    }

    [Fact]
    public async Task ChunkAsync_LongTable_IsSplitOnRowsWithTheHeaderRepeated()
    {
        var table = new StringBuilder("# Results\n\n| Region | Quarter | Revenue in millions |\n| --- | --- | --- |\n");
        for (int i = 0; i < 120; i++)
            table.Append($"| Region number {i} | Q{i % 4 + 1} | {100 + i} |\n");

        var chunks = await ChunkAsync(table.ToString(), maxChunkSize: 200);

        chunks.Should().HaveCountGreaterThan(1);
        foreach (var chunk in chunks)
        {
            chunk.Content.Should().Contain("| Region | Quarter | Revenue in millions |", "every piece keeps the columns");
            chunk.Content.Split('\n').Where(l => l.StartsWith("| Region number")).Should()
                .OnlyContain(l => l.TrimEnd().EndsWith('|'), "no row is cut in half");
        }

        string all = string.Join("\n", chunks.Select(c => c.Content));
        for (int i = 0; i < 120; i++)
            all.Should().Contain($"| Region number {i} |");
    }

    [Fact]
    public async Task ChunkAsync_OversizeSectionWithBreadcrumb_StaysWithinTheBudget()
    {
        string heading = "# " + string.Join(" ", Enumerable.Repeat("Very long section heading", 6));
        string body = string.Join(" ", Enumerable.Repeat("The warehouse shipped orders on time all week.", 80));

        var chunks = await ChunkAsync($"{heading}\n\n{body}\n", maxChunkSize: 128);

        chunks.Should().HaveCountGreaterThan(1);
        chunks.Should().OnlyContain(c => _counter.CountTokens(c.Content) <= 128,
            "the breadcrumb is prepended to every piece, so its tokens are reserved first");
    }
}
