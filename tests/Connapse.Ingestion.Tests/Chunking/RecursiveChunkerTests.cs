using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Chunking;
using Connapse.Ingestion.Utilities;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Chunking;

[Trait("Category", "Unit")]
public class RecursiveChunkerTests
{
    private readonly RecursiveChunker _chunker = new(new TiktokenTokenCounter());

    [Fact]
    public void Name_ReturnsRecursive()
    {
        _chunker.Name.Should().Be("Recursive");
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
    }

    [Fact]
    public async Task ChunkAsync_ParagraphSeparatedContent_SplitsAtParagraphs()
    {
        var content = "Paragraph one.\n\nParagraph two.\n\nParagraph three.";
        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 20,
            Overlap = 5,
            MinChunkSize = 5,
            RecursiveSeparators = new[] { "\n\n", "\n", ". ", " " }
        };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        result.Should().NotBeEmpty();
        result.Should().OnlyContain(chunk => chunk.TokenCount <= settings.MaxChunkSize);
    }

    [Fact]
    public async Task ChunkAsync_UsesDefaultSeparatorsIfNoneProvided()
    {
        var content = "Line one.\n\nLine two.\n\nLine three.";
        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 20,
            Overlap = 5,
            MinChunkSize = 5,
            RecursiveSeparators = Array.Empty<string>() // No separators specified
        };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        // Should use default separators: ["\n\n", "\n", ". ", " "]
        result.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ChunkAsync_RespectsMinChunkSize()
    {
        var content = "Tiny.";
        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 100,
            Overlap = 10,
            MinChunkSize = 50 // Content is smaller than min
        };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        // Small documents are returned as a single chunk even when below MinChunkSize
        // to avoid silently discarding valid content
        result.Should().HaveCount(1);
        result[0].Content.Should().Be(content.Trim());
    }

    [Fact]
    public async Task ChunkAsync_PreservesMetadata()
    {
        var metadata = new Dictionary<string, string>
        {
            ["FileType"] = "Markdown",
            ["Source"] = "Test"
        };
        var content = "Some content here for testing.";
        var parsedDoc = new ParsedDocument(content, metadata, new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 50, Overlap = 10, MinChunkSize = 5 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        result.Should().NotBeEmpty();
        foreach (var chunk in result)
        {
            chunk.Metadata.Should().ContainKey("FileType");
            chunk.Metadata.Should().ContainKey("Source");
            chunk.Metadata.Should().ContainKey("ChunkingStrategy");
            chunk.Metadata["ChunkingStrategy"].Should().Be("Recursive");
            chunk.Metadata.Should().ContainKey("ChunkIndex");
        }
    }

    [Fact]
    public async Task ChunkAsync_ChunksHaveSequentialIndices()
    {
        var content = string.Join("\n\n", Enumerable.Range(1, 10).Select(i => $"Paragraph {i}."));
        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 20, Overlap = 5, MinChunkSize = 5 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        for (int i = 0; i < result.Count; i++)
        {
            result[i].ChunkIndex.Should().Be(i);
        }
    }

    [Fact]
    public async Task ChunkAsync_TriesMultipleSeparatorsHierarchically()
    {
        // Content without double newlines, should fall back to single newline
        var content = "Line 1\nLine 2\nLine 3\nLine 4\nLine 5\nLine 6\nLine 7";
        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 15,
            Overlap = 3,
            MinChunkSize = 5,
            RecursiveSeparators = new[] { "\n\n", "\n", " " } // Will use \n since no \n\n
        };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        result.Should().NotBeEmpty();
        result.Should().OnlyContain(chunk => chunk.TokenCount <= settings.MaxChunkSize);
    }

    [Fact]
    public async Task ChunkAsync_HandlesOverlap()
    {
        var content = "First sentence. Second sentence. Third sentence. Fourth sentence.";
        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 8,
            Overlap = 3,
            MinChunkSize = 1,
            RecursiveSeparators = new[] { ". ", " " }
        };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        result.Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public async Task ChunkAsync_ChunksHaveValidOffsets()
    {
        var content = "Test content with multiple words here.";
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
        var content = string.Join("\n\n", Enumerable.Range(1, 100).Select(i => $"Paragraph {i} content."));
        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 20, Overlap = 5, MinChunkSize = 5 };

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await _chunker.ChunkAsync(parsedDoc, settings, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ChunkAsync_HandlesLongContentWithoutSeparators()
    {
        // Content with no separators at all - should fall back to character splitting
        var content = new string('x', 500);
        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 50,
            Overlap = 10,
            MinChunkSize = 10,
            RecursiveSeparators = new[] { "\n\n", "\n", ". " }
        };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        result.Should().HaveCountGreaterThan(1);
        result.Should().OnlyContain(chunk => chunk.TokenCount <= settings.MaxChunkSize + 10); // Allow small margin
    }

    [Fact]
    public async Task ChunkAsync_TrimsWhitespace()
    {
        var content = "  First line.  \n\n  Second line.  ";
        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 50, Overlap = 10, MinChunkSize = 5 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        result.Should().NotBeEmpty();
        result.Should().OnlyContain(chunk =>
            !chunk.Content.StartsWith(' ') && !chunk.Content.EndsWith(' '));
    }

    [Fact]
    public async Task ChunkAsync_HandlesCustomSeparators()
    {
        var content = "Section1||Section2||Section3||Section4";
        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 15,
            Overlap = 3,
            MinChunkSize = 5,
            RecursiveSeparators = new[] { "||", " " }
        };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        result.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ChunkAsync_AdjacentChunksShareOverlap_WhenChunksAreMultiUnit()
    {
        string content = string.Join(" ",
            Enumerable.Range(1, 12).Select(i => $"Item {i} is short."));
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 20,
            Overlap = 8,
            MinChunkSize = 1,
            RecursiveSeparators = new[] { ". ", " " }
        };

        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().HaveCountGreaterThan(1, "test inputs should force multi-chunk output");
        for (int i = 0; i < result.Count - 1; i++)
        {
            string prev = result[i].Content;
            string next = result[i + 1].Content;

            bool found = false;
            int maxLen = Math.Min(prev.Length, next.Length);
            for (int len = maxLen; len >= 6; len--)
            {
                string suffix = prev.Substring(prev.Length - len);
                if (next.StartsWith(suffix, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            found.Should().BeTrue(
                $"chunks {i}->{i + 1} should share overlap text. " +
                $"prev tail='...{prev.Substring(Math.Max(0, prev.Length - 30))}' | " +
                $"next head='{next.Substring(0, Math.Min(next.Length, 30))}...'");
        }
    }

    [Fact]
    public async Task ChunkAsync_DoesNotSilentlyDropContentBetweenLargeChunks()
    {
        string longA = string.Join(" ", Enumerable.Repeat("alpha", 30));
        string tinyB = "x marker.";
        string longC = string.Join(" ", Enumerable.Repeat("charlie", 30));
        string content = longA + "\n\n" + tinyB + "\n\n" + longC;

        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 30,
            Overlap = 0,
            MinChunkSize = 5,
            RecursiveSeparators = new[] { "\n\n", "\n", ". ", " " }
        };

        var result = await _chunker.ChunkAsync(doc, settings);

        bool tinyPresent = result.Any(c => c.Content.Contains("marker"));
        tinyPresent.Should().BeTrue(
            "MinChunkSize must not silently drop document content; " +
            "small segments must be merged into a neighbour, not discarded");
    }

    [Fact]
    public async Task ChunkAsync_OffsetsRoundTripWithSourceText()
    {
        string content = string.Join("\n\n",
            Enumerable.Range(1, 8).Select(i => $"Paragraph number {i} with some words."));
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 10,
            Overlap = 3,
            MinChunkSize = 1,
            RecursiveSeparators = new[] { "\n\n", "\n", ". ", " " }
        };

        var result = await _chunker.ChunkAsync(doc, settings);

        foreach (ChunkInfo c in result)
        {
            c.StartOffset.Should().BeGreaterThanOrEqualTo(0);
            c.EndOffset.Should().BeLessThanOrEqualTo(content.Length,
                "endOffset must never exceed the source length");
            c.EndOffset.Should().BeGreaterThan(c.StartOffset);

            string slice = content.Substring(c.StartOffset, c.EndOffset - c.StartOffset);
            slice.Trim().Should().Be(c.Content,
                "the substring at the recorded offsets should equal the chunk's content");
        }
    }

    [Fact]
    public async Task ChunkAsync_OffsetsRoundTripEvenWhenMergeForwardFires()
    {
        // A counter that pretends "x marker." is 0 tokens, so it falls below MinChunkSize=5
        // and triggers MergeForwardSmallChunks. All other text gets its real tiktoken count.
        // MaxChunkSize is small enough that the splitter actually splits at "\n\n" rather
        // than swallowing the whole document into a single pre-merge chunk.
        var stubCounter = new MergeForwardOffsetStubCounter();
        var chunker = new RecursiveChunker(stubCounter);

        string content =
            "Paragraph one with several distinct words.\n\n" +
            "x marker.\n\n" +
            "Paragraph three with several distinct words.";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 8,
            Overlap = 0,
            MinChunkSize = 5,
            RecursiveSeparators = new[] { "\n\n", "\n", ". ", " " }
        };

        var result = await chunker.ChunkAsync(doc, settings);

        bool tinyMerged = result.Any(c => c.Content.Contains("marker"));
        tinyMerged.Should().BeTrue("the small chunk should have been merged into a neighbour, not dropped");

        foreach (ChunkInfo c in result)
        {
            c.StartOffset.Should().BeGreaterThanOrEqualTo(0);
            c.EndOffset.Should().BeLessThanOrEqualTo(content.Length);
            string slice = content.Substring(c.StartOffset, c.EndOffset - c.StartOffset);
            slice.Trim().Should().Be(c.Content,
                "round-trip must hold even when MergeForward fires (separator must be re-included from source)");
        }
    }

    private sealed class MergeForwardOffsetStubCounter : ITokenCounter
    {
        private readonly TiktokenTokenCounter _real = new();

        public int CountTokens(string text)
        {
            // Only the lone "x marker." segment reads as 0 tokens, so the
            // recursive splitter still splits the whole document at \n\n.
            if (text == "x marker.") return 0;
            return _real.CountTokens(text ?? string.Empty);
        }

        public int GetIndexAtTokenCount(string text, int tokenCount)
            => _real.GetIndexAtTokenCount(text, tokenCount);
    }
}
