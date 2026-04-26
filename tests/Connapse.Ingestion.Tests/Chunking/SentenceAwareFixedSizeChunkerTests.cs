using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Chunking;
using Connapse.Ingestion.Utilities;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Chunking;

[Trait("Category", "Unit")]
public class SentenceAwareFixedSizeChunkerTests
{
    private readonly SentenceAwareFixedSizeChunker _chunker;

    public SentenceAwareFixedSizeChunkerTests()
    {
        TiktokenTokenCounter counter = new();
        PragmaticSentenceSegmenter segmenter = new();
        RecursiveChunker recursive = new(counter);
        _chunker = new SentenceAwareFixedSizeChunker(counter, segmenter, recursive);
    }

    [Fact]
    public void Name_IsSentenceAwareFixedSize()
    {
        _chunker.Name.Should().Be("SentenceAwareFixedSize");
    }

    [Fact]
    public async Task ChunkAsync_EmptyContent_ReturnsEmpty()
    {
        var doc = new ParsedDocument("", new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 100, Overlap = 10, MinChunkSize = 1 };
        var result = await _chunker.ChunkAsync(doc, settings);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ChunkAsync_SmallContent_ReturnsSingleChunk()
    {
        string content = "First sentence. Second sentence.";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 100, Overlap = 0, MinChunkSize = 1 };
        var result = await _chunker.ChunkAsync(doc, settings);
        result.Should().HaveCount(1);
        result[0].Content.Should().Contain("First sentence");
        result[0].Content.Should().Contain("Second sentence");
    }

    [Fact]
    public async Task ChunkAsync_NeverSplitsMidSentence()
    {
        string content = string.Join(" ",
            Enumerable.Range(1, 30).Select(i => $"Sentence number {i} with several words."));
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 30, Overlap = 5, MinChunkSize = 1 };
        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().HaveCountGreaterThan(1);
        // Every chunk's content should end with sentence-terminating punctuation
        // (after trim), proving no chunk was cut mid-sentence.
        foreach (ChunkInfo c in result)
        {
            string trimmed = c.Content.Trim();
            char lastChar = trimmed[trimmed.Length - 1];
            (lastChar == '.' || lastChar == '!' || lastChar == '?').Should().BeTrue(
                $"chunk {c.ChunkIndex} content '{trimmed}' should end with sentence punctuation");
        }
    }

    [Fact]
    public async Task ChunkAsync_OffsetsRoundTripWithSourceText()
    {
        string content = string.Join(" ",
            Enumerable.Range(1, 20).Select(i => $"Sentence number {i} with several words."));
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 25, Overlap = 5, MinChunkSize = 1 };
        var result = await _chunker.ChunkAsync(doc, settings);

        foreach (ChunkInfo c in result)
        {
            c.StartOffset.Should().BeGreaterThanOrEqualTo(0);
            c.EndOffset.Should().BeLessThanOrEqualTo(content.Length);
            c.EndOffset.Should().BeGreaterThan(c.StartOffset);
            string slice = content.Substring(c.StartOffset, c.EndOffset - c.StartOffset);
            slice.Trim().Should().Be(c.Content);
        }
    }

    [Fact]
    public async Task ChunkAsync_AdjacentChunksOverlap()
    {
        string content = string.Join(" ",
            Enumerable.Range(1, 30).Select(i => $"S{i} a b c d e."));
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 30, Overlap = 8, MinChunkSize = 1 };
        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().HaveCountGreaterThan(1);
        for (int i = 1; i < result.Count; i++)
        {
            result[i].StartOffset.Should().BeLessThan(result[i - 1].EndOffset,
                $"chunk {i} should overlap chunk {i - 1}");
        }
    }

    [Fact]
    public async Task ChunkAsync_HugeSentence_DelegatesToRecursive()
    {
        // A single "sentence" of 200 words with no internal punctuation. Should be
        // delegated to RecursiveChunker for hierarchical sub-splitting.
        string content = string.Join(" ", Enumerable.Repeat("alpha", 200)) + ".";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 30,
            Overlap = 0,
            MinChunkSize = 1,
            RecursiveSeparators = new[] { ". ", " " }
        };
        var result = await _chunker.ChunkAsync(doc, settings);
        result.Should().HaveCountGreaterThan(1);
        foreach (ChunkInfo c in result)
        {
            c.StartOffset.Should().BeGreaterThanOrEqualTo(0);
            c.EndOffset.Should().BeLessThanOrEqualTo(content.Length);
        }
    }

    [Fact]
    public async Task ChunkAsync_DoesNotSilentlyDropSubMinChunks()
    {
        string content = "First long sentence with several content words. Tiny. "
            + "Last long sentence with several content words.";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        // MinChunkSize=20 forces the standalone "Tiny." (~2 tokens) to be merged.
        var settings = new ChunkingSettings { MaxChunkSize = 12, Overlap = 0, MinChunkSize = 20 };
        var result = await _chunker.ChunkAsync(doc, settings);
        bool tinyPresent = result.Any(c => c.Content.Contains("Tiny"));
        tinyPresent.Should().BeTrue("MinChunkSize must merge sub-min chunks, not drop them");
    }
}
