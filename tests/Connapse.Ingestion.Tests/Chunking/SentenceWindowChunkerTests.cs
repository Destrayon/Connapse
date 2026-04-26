using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Chunking;
using Connapse.Ingestion.Utilities;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Chunking;

[Trait("Category", "Unit")]
public class SentenceWindowChunkerTests
{
    private readonly SentenceWindowChunker _chunker;

    public SentenceWindowChunkerTests()
    {
        TiktokenTokenCounter counter = new();
        PragmaticSentenceSegmenter segmenter = new();
        _chunker = new SentenceWindowChunker(counter, segmenter);
    }

    [Fact]
    public void Name_IsSentenceWindow()
    {
        _chunker.Name.Should().Be("SentenceWindow");
    }

    [Fact]
    public async Task ChunkAsync_EmptyContent_ReturnsEmpty()
    {
        var doc = new ParsedDocument("", new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 100, MinChunkSize = 1 };
        var result = await _chunker.ChunkAsync(doc, settings);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ChunkAsync_OneChunkPerSentence()
    {
        string content = "First sentence. Second sentence. Third sentence.";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 1000, MinChunkSize = 1, SentenceWindowSize = 1 };

        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().HaveCount(3);
        result[0].Content.Should().Contain("First sentence");
        result[1].Content.Should().Contain("Second sentence");
        result[2].Content.Should().Contain("Third sentence");
    }

    [Fact]
    public async Task ChunkAsync_DefaultWindow_ProducesNeighborText()
    {
        // 5 sentences, window = 1 → middle sentence's window contains 3 sentences (i-1, i, i+1).
        string content = "Alpha. Beta. Gamma. Delta. Epsilon.";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 1000, MinChunkSize = 1, SentenceWindowSize = 1 };

        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().HaveCount(5);
        // Index 2 = "Gamma." — its window should contain Beta + Gamma + Delta.
        ChunkInfo middle = result[2];
        middle.Metadata.Should().ContainKey("window");
        middle.Metadata["window"].Should().Contain("Beta");
        middle.Metadata["window"].Should().Contain("Gamma");
        middle.Metadata["window"].Should().Contain("Delta");
    }

    [Fact]
    public async Task ChunkAsync_FirstSentence_WindowTruncatesAtBoundary()
    {
        string content = "Alpha. Beta. Gamma.";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 1000, MinChunkSize = 1, SentenceWindowSize = 2 };

        var result = await _chunker.ChunkAsync(doc, settings);

        ChunkInfo first = result[0];
        first.Metadata["window"].Should().Contain("Alpha");
        first.Metadata["window"].Should().Contain("Beta");
        first.Metadata["window"].Should().Contain("Gamma");
        // Window can't extend left past index 0; truncation is silent.
    }

    [Fact]
    public async Task ChunkAsync_SingleSentence_SingleChunk()
    {
        string content = "Just one sentence.";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 1000, MinChunkSize = 1, SentenceWindowSize = 3 };

        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().HaveCount(1);
        result[0].Metadata["window"].Should().Contain("Just one sentence");
        result[0].Metadata["original_text"].Should().Contain("Just one sentence");
    }

    [Fact]
    public async Task ChunkAsync_ContentIsTheSentence_NotTheWindow()
    {
        // Critical: embedding consumers read .Content. It MUST be the precise sentence,
        // not the wider window — otherwise we re-create the original problem semantic-window
        // is meant to fix.
        string content = "Alpha. Beta. Gamma.";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 1000, MinChunkSize = 1, SentenceWindowSize = 1 };

        var result = await _chunker.ChunkAsync(doc, settings);

        result[1].Content.Trim().Should().Be("Beta.");
        result[1].Metadata["window"].Should().NotBe("Beta.");  // window is wider
    }

    [Fact]
    public async Task ChunkAsync_BypassesMinChunkSize()
    {
        // Sentence-window chunks are intentionally tiny; MinChunkSize=100 must NOT
        // merge them via the merge-forward post-pass other chunkers run.
        // (Fixture uses full short sentences because PragmaticSegmenter treats
        // single-letter "A. B. C." as an initialism and merges them into one sentence.)
        string content = "Cats purr. Dogs bark. Birds sing. Fish swim. Bees buzz.";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 1000, MinChunkSize = 100, SentenceWindowSize = 1 };

        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().HaveCountGreaterThanOrEqualTo(3);  // PragmaticSegmenter may merge edges; just confirm not 1.
    }

    [Fact]
    public async Task ChunkAsync_MetadataContainsRequiredKeys()
    {
        string content = "Alpha. Beta. Gamma.";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 1000, MinChunkSize = 1, SentenceWindowSize = 2 };

        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().AllSatisfy(c =>
        {
            c.Metadata["ChunkingStrategy"].Should().Be("SentenceWindow");
            c.Metadata.Should().ContainKey("ChunkIndex");
            c.Metadata.Should().ContainKey("window");
            c.Metadata.Should().ContainKey("original_text");
            c.Metadata.Should().ContainKey("window_size");
            c.Metadata["window_size"].Should().Be("2");
        });
    }
}
