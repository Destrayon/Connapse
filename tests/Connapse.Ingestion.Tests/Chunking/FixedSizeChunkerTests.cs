using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Chunking;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Chunking;

public class FixedSizeChunkerTests
{
    private readonly FixedSizeChunker _chunker = new();

    [Fact]
    public void Name_ReturnsFixedSize()
    {
        _chunker.Name.Should().Be("FixedSize");
    }

    [Fact]
    public async Task ChunkAsync_EmptyContent_ReturnsEmptyList()
    {
        var parsedDoc = new ParsedDocument("", new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 100, Overlap = 20, MinChunkSize = 10 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ChunkAsync_SmallContent_ReturnsSingleChunk()
    {
        var content = "This is a small piece of text.";
        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 100, Overlap = 20, MinChunkSize = 5 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        result.Should().ContainSingle();
        result[0].Content.Should().Be(content.Trim());
        result[0].ChunkIndex.Should().Be(0);
        result[0].TokenCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ChunkAsync_LargeContent_SplitsIntoMultipleChunks()
    {
        // Create content that will require multiple chunks
        var sentences = Enumerable.Range(1, 50)
            .Select(i => $"This is sentence number {i} with some meaningful content.");
        var content = string.Join(" ", sentences);

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 50, Overlap = 10, MinChunkSize = 10 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        result.Should().HaveCountGreaterThan(1);
        result.Should().OnlyContain(chunk => chunk.TokenCount <= settings.MaxChunkSize);
    }

    [Fact]
    public async Task ChunkAsync_ChunksHaveCorrectIndices()
    {
        var sentences = Enumerable.Range(1, 20)
            .Select(i => $"Sentence {i}.");
        var content = string.Join(" ", sentences);

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 20, Overlap = 5, MinChunkSize = 5 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        for (int i = 0; i < result.Count; i++)
        {
            result[i].ChunkIndex.Should().Be(i);
        }
    }

    [Fact]
    public async Task ChunkAsync_IncludesOverlapBetweenChunks()
    {
        var content = "First sentence. Second sentence. Third sentence. Fourth sentence. Fifth sentence.";
        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 15, Overlap = 5, MinChunkSize = 5 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        // With overlap, adjacent chunks should have some overlapping content
        result.Should().HaveCountGreaterThan(1);

        // Verify that later chunks start before the previous chunk ended
        for (int i = 1; i < result.Count; i++)
        {
            result[i].StartOffset.Should().BeLessThan(result[i - 1].EndOffset);
        }
    }

    [Fact]
    public async Task ChunkAsync_RespectsMinChunkSize()
    {
        var content = "Short. Text.";
        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 10, Overlap = 2, MinChunkSize = 50 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        // Content is too small to meet MinChunkSize, should still include it as final chunk
        result.Should().ContainSingle();
    }

    [Fact]
    public async Task ChunkAsync_BreaksAtNaturalBoundaries()
    {
        var content = "Paragraph one.\n\nParagraph two.\n\nParagraph three.";
        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 20, Overlap = 3, MinChunkSize = 5 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        // Should try to break at paragraph boundaries (double newlines)
        result.Should().NotBeEmpty();
        result.Should().OnlyContain(chunk => !string.IsNullOrWhiteSpace(chunk.Content));
    }

    [Fact]
    public async Task ChunkAsync_PreservesMetadata()
    {
        var metadata = new Dictionary<string, string>
        {
            ["FileType"] = "Text",
            ["Author"] = "Test"
        };
        var parsedDoc = new ParsedDocument("Some content here", metadata, new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 50, Overlap = 10, MinChunkSize = 5 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        result.Should().NotBeEmpty();
        foreach (var chunk in result)
        {
            chunk.Metadata.Should().ContainKey("FileType");
            chunk.Metadata.Should().ContainKey("Author");
            chunk.Metadata.Should().ContainKey("ChunkingStrategy");
            chunk.Metadata.Should().ContainKey("ChunkIndex");
        }
    }

    [Fact]
    public async Task ChunkAsync_HandlesOverlapLargerThanChunkSize()
    {
        var content = "This is some test content that will be chunked.";
        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 20,
            Overlap = 50, // Overlap larger than chunk size
            MinChunkSize = 5
        };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        // Should auto-adjust overlap to 25% of chunk size
        result.Should().NotBeEmpty();

        // Ensure it doesn't get stuck in infinite loop
        result.Should().HaveCountGreaterThan(0);
    }

    [Fact]
    public async Task ChunkAsync_ChunksHaveValidOffsets()
    {
        var content = "This is a test sentence for offset validation.";
        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 30, Overlap = 5, MinChunkSize = 5 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        foreach (var chunk in result)
        {
            chunk.StartOffset.Should().BeGreaterThanOrEqualTo(0);
            chunk.EndOffset.Should().BeGreaterThan(chunk.StartOffset);
            chunk.EndOffset.Should().BeLessThanOrEqualTo(content.Length);
        }
    }

    [Fact]
    public async Task ChunkAsync_SupportsCancellation()
    {
        var content = string.Join(" ", Enumerable.Range(1, 1000).Select(i => $"Sentence {i}."));
        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 20, Overlap = 5, MinChunkSize = 5 };

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await _chunker.ChunkAsync(parsedDoc, settings, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ChunkAsync_TrimsWhitespace()
    {
        var content = "  Sentence with leading spaces.  \n  Another sentence.  ";
        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 50, Overlap = 10, MinChunkSize = 5 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        result.Should().NotBeEmpty();
        result.Should().OnlyContain(chunk =>
            !chunk.Content.StartsWith(' ') && !chunk.Content.EndsWith(' '));
    }
}
