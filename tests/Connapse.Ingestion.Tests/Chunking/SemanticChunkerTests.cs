using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Chunking;
using Connapse.Ingestion.Utilities;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Ingestion.Tests.Chunking;

[Trait("Category", "Unit")]
public class SemanticChunkerTests
{
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly SemanticChunker _chunker;

    public SemanticChunkerTests()
    {
        _embeddingProvider = Substitute.For<IEmbeddingProvider>();
        _embeddingProvider.Dimensions.Returns(3);
        _chunker = new SemanticChunker(
            _embeddingProvider,
            new TiktokenTokenCounter(),
            new PragmaticSentenceSegmenter(),
            new RecursiveChunker(new TiktokenTokenCounter()));
    }

    /// <summary>
    /// Helper: configures EmbedBatchAsync to return distinct embeddings per sentence.
    /// Each embedding is a 3-dimensional vector with a unique direction so cosine similarity
    /// between adjacent embeddings can be controlled via the angle parameter.
    /// </summary>
    private void SetupEmbeddings(int sentenceCount, bool highSimilarity = true)
    {
        _embeddingProvider.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var texts = callInfo.Arg<IEnumerable<string>>().ToArray();
                var embeddings = new List<float[]>();
                for (int i = 0; i < texts.Length; i++)
                {
                    if (highSimilarity)
                    {
                        // All vectors point in similar directions (high cosine similarity)
                        embeddings.Add(new float[] { 1.0f, 0.1f * i, 0.0f });
                    }
                    else
                    {
                        // Alternating orthogonal vectors (low cosine similarity between adjacent)
                        embeddings.Add(i % 2 == 0
                            ? new float[] { 1.0f, 0.0f, 0.0f }
                            : new float[] { 0.0f, 1.0f, 0.0f });
                    }
                }
                return Task.FromResult<IReadOnlyList<float[]>>(embeddings);
            });
    }

    /// <summary>
    /// Helper: configures EmbedBatchAsync with explicit embeddings for fine-grained control.
    /// </summary>
    private void SetupExplicitEmbeddings(float[][] embeddings)
    {
        _embeddingProvider.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<float[]>>(embeddings));
    }

    [Fact]
    public void Name_ReturnsSemantic()
    {
        _chunker.Name.Should().Be("Semantic");
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
    public async Task ChunkAsync_WhitespaceOnlyContent_ReturnsEmptyList()
    {
        var parsedDoc = new ParsedDocument("   \n\t  ", new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 100, Overlap = 20, MinChunkSize = 10 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ChunkAsync_SingleSentence_ReturnsSingleChunkWithoutEmbedding()
    {
        var content = "This is a single sentence without any sentence-ending punctuation followed by a space";
        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 500, Overlap = 20, MinChunkSize = 1 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        result.Should().ContainSingle();
        result[0].Content.Should().Be(content.Trim());
        result[0].ChunkIndex.Should().Be(0);
        result[0].TokenCount.Should().BeGreaterThan(0);
        // Single sentence skips embedding — PrecomputedEmbedding is null
        result[0].PrecomputedEmbedding.Should().BeNull();
    }

    [Fact]
    public async Task ChunkAsync_SingleSentence_DoesNotCallEmbeddingProvider()
    {
        var content = "Just one sentence here";
        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 500, Overlap = 20, MinChunkSize = 1 };

        await _chunker.ChunkAsync(parsedDoc, settings);

        await _embeddingProvider.DidNotReceive()
            .EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChunkAsync_MultipleSentences_HighSimilarity_GroupsIntoFewerChunks()
    {
        // Five semantically similar sentences — should group together
        var content = "First sentence here. Second sentence here. Third sentence here. Fourth sentence here. Fifth sentence here. ";
        SetupEmbeddings(5, highSimilarity: true);

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 500, Overlap = 0, MinChunkSize = 1 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        // With high similarity, sentences should be grouped (fewer chunks than sentences)
        result.Should().NotBeEmpty();
        result.Count.Should().BeLessThanOrEqualTo(5);
    }

    [Fact]
    public async Task ChunkAsync_MultipleSentences_LowSimilarity_SplitsIntoMoreChunks()
    {
        // 6 sentences with a clear topic shift in the middle.
        // With 5 similarity values the adaptive 80th-percentile threshold kicks in.
        // Most pairs are highly similar (~1.0) but one pair is orthogonal (0.0),
        // so the 80th-percentile threshold is high and the low-similarity pair triggers a split.
        var content = "Alpha topic here. Beta topic here. Gamma topic here. Delta topic here. Epsilon topic here. Zeta topic here. ";

        SetupExplicitEmbeddings(new[]
        {
            new float[] { 1.0f, 0.0f, 0.0f },
            new float[] { 0.99f, 0.14f, 0.0f },  // similar to [0]
            new float[] { 0.98f, 0.20f, 0.0f },  // similar to [1]
            new float[] { 0.0f, 0.0f, 1.0f },    // orthogonal — topic shift
            new float[] { 0.0f, 0.14f, 0.99f },  // similar to [3]
            new float[] { 0.0f, 0.20f, 0.98f }   // similar to [4]
        });

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 500, Overlap = 0, MinChunkSize = 1 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        // With a clear topic boundary, should create at least 2 chunks
        result.Count.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task ChunkAsync_LargeChunk_ExceedingMaxChunkSize_GetsSplitFurther()
    {
        // Create content where all sentences group into one oversized chunk
        var sentences = Enumerable.Range(1, 30)
            .Select(i => $"This is detailed sentence number {i} with enough content to be substantial. ");
        var content = string.Join("", sentences);

        SetupEmbeddings(30, highSimilarity: true);

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 50, Overlap = 0, MinChunkSize = 5 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        // When a semantic group exceeds MaxChunkSize, it gets split further
        result.Should().HaveCountGreaterThan(1);
        // Sub-chunks from oversized groups have null PrecomputedEmbedding
        result.Should().Contain(c => c.PrecomputedEmbedding == null);
    }

    [Fact]
    public async Task ChunkAsync_MinChunkSizeFiltering_SkipsTinyChunks()
    {
        // Two sentences: one short, one long, with low similarity so they split
        var content = "Hi. This is a significantly longer sentence that should exceed the minimum chunk size threshold easily. ";

        // Orthogonal embeddings to force a split
        SetupExplicitEmbeddings(new[]
        {
            new float[] { 1.0f, 0.0f, 0.0f },
            new float[] { 0.0f, 1.0f, 0.0f }
        });

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        // "Hi" is ~1 token (2 chars / 4); MinChunkSize = 5 should filter it out
        var settings = new ChunkingSettings { MaxChunkSize = 500, Overlap = 0, MinChunkSize = 5 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        // The short "Hi" chunk should be filtered out by MinChunkSize
        result.Should().NotBeEmpty();
        result.Should().OnlyContain(c => c.TokenCount >= settings.MinChunkSize);
    }

    [Fact]
    public async Task ChunkAsync_AllChunksBelowMinSize_MergesIntoSingleChunk()
    {
        // Three short sentences — Pragmatic segmenter splits these into 3, but each is too
        // small to satisfy MinChunkSize. Merge-forward post-pass coalesces them into a single
        // chunk that contains all the text. (Previous behavior was a separate fallback path
        // that returned the whole content with a mean-pooled embedding — superseded by
        // merge-forward which discards embeddings since the merged span no longer maps cleanly
        // to a sentence-embedding range.)
        var content = "Apple. Banana. Cherry. ";

        SetupExplicitEmbeddings(new[]
        {
            new float[] { 1.0f, 0.0f, 0.0f },
            new float[] { 0.0f, 1.0f, 0.0f },
            new float[] { 0.0f, 0.0f, 1.0f }
        });

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 500, Overlap = 0, MinChunkSize = 50 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        // Merge-forward collapses all three small chunks into one — content is preserved.
        result.Should().ContainSingle();
        result[0].Content.Should().Contain("Apple").And.Contain("Banana").And.Contain("Cherry");
        result[0].ChunkIndex.Should().Be(0);
        // Merged chunks no longer carry a precomputed embedding — pipeline will re-embed.
        result[0].PrecomputedEmbedding.Should().BeNull();
    }

    [Fact]
    public async Task ChunkAsync_PreservesMetadata()
    {
        var metadata = new Dictionary<string, string>
        {
            ["FileType"] = "Text",
            ["Source"] = "Test"
        };
        var content = "First topic sentence. Second topic sentence. ";
        SetupEmbeddings(2, highSimilarity: true);

        var parsedDoc = new ParsedDocument(content, metadata, new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 500, Overlap = 0, MinChunkSize = 1 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        result.Should().NotBeEmpty();
        foreach (var chunk in result)
        {
            chunk.Metadata.Should().ContainKey("FileType");
            chunk.Metadata.Should().ContainKey("Source");
            chunk.Metadata.Should().ContainKey("ChunkingStrategy");
            chunk.Metadata["ChunkingStrategy"].Should().Be("Semantic");
            chunk.Metadata.Should().ContainKey("ChunkIndex");
        }
    }

    [Fact]
    public async Task ChunkAsync_ChunksHaveSequentialIndices()
    {
        var sentences = Enumerable.Range(1, 10)
            .Select(i => $"Sentence about topic {i} with unique content. ");
        var content = string.Join("", sentences);

        SetupEmbeddings(10, highSimilarity: false);

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 500, Overlap = 0, MinChunkSize = 1 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        for (int i = 0; i < result.Count; i++)
        {
            result[i].ChunkIndex.Should().Be(i);
        }
    }

    [Fact]
    public async Task ChunkAsync_ChunksHaveValidOffsets()
    {
        var content = "First sentence about cats. Second sentence about dogs. Third sentence about birds. ";
        SetupEmbeddings(3, highSimilarity: true);

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 500, Overlap = 0, MinChunkSize = 1 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        foreach (var chunk in result)
        {
            chunk.StartOffset.Should().BeGreaterThanOrEqualTo(0);
            chunk.EndOffset.Should().BeGreaterThan(chunk.StartOffset);
        }
    }

    [Fact]
    public async Task ChunkAsync_TrimsWhitespace()
    {
        var content = "  First sentence with spaces. Second sentence with spaces.  ";
        SetupEmbeddings(2, highSimilarity: true);

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 500, Overlap = 0, MinChunkSize = 1 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        result.Should().NotBeEmpty();
        result.Should().OnlyContain(chunk =>
            !chunk.Content.StartsWith(' ') && !chunk.Content.EndsWith(' '));
    }

    [Fact]
    public async Task ChunkAsync_SupportsCancellation()
    {
        var sentences = Enumerable.Range(1, 20)
            .Select(i => $"Sentence number {i} about various topics. ");
        var content = string.Join("", sentences);

        // Make EmbedBatchAsync succeed but cancellation should trigger during chunk creation
        SetupEmbeddings(20, highSimilarity: false);

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 500, Overlap = 0, MinChunkSize = 1 };

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // EmbedBatchAsync should receive the cancelled token and throw
        _embeddingProvider.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<float[]>>(ci =>
            {
                ci.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return Array.Empty<float[]>();
            });

        var act = async () => await _chunker.ChunkAsync(parsedDoc, settings, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ChunkAsync_NormalSizedChunks_HavePrecomputedEmbedding()
    {
        var content = "First important topic. Second important topic. ";
        SetupEmbeddings(2, highSimilarity: true);

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 500, Overlap = 0, MinChunkSize = 1 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        result.Should().NotBeEmpty();
        // Normal chunks (not sub-split) should have precomputed mean-pooled embeddings
        result.Should().Contain(c => c.PrecomputedEmbedding != null);
    }

    [Fact]
    public async Task ChunkAsync_PrecomputedEmbedding_HasCorrectDimensions()
    {
        var content = "First important topic. Second important topic. ";
        SetupEmbeddings(2, highSimilarity: true);

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 500, Overlap = 0, MinChunkSize = 1 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        var chunksWithEmbeddings = result.Where(c => c.PrecomputedEmbedding != null).ToList();
        chunksWithEmbeddings.Should().NotBeEmpty();
        foreach (var chunk in chunksWithEmbeddings)
        {
            chunk.PrecomputedEmbedding!.Length.Should().Be(3); // 3-dimensional test embeddings
        }
    }

    [Fact]
    public async Task ChunkAsync_CallsEmbedBatchAsync_WithAllSentences()
    {
        var content = "Alpha sentence here. Beta sentence here. Gamma sentence here. ";
        SetupEmbeddings(3, highSimilarity: true);

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 500, Overlap = 0, MinChunkSize = 1 };

        await _chunker.ChunkAsync(parsedDoc, settings);

        // Should call EmbedBatchAsync exactly once with all sentences
        await _embeddingProvider.Received(1)
            .EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChunkAsync_TokenCountsArePositive()
    {
        var content = "First sentence about topic A. Second sentence about topic B. Third sentence about topic C. ";
        SetupEmbeddings(3, highSimilarity: true);

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 500, Overlap = 0, MinChunkSize = 1 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        result.Should().NotBeEmpty();
        result.Should().OnlyContain(c => c.TokenCount > 0);
    }

    [Fact]
    public async Task ChunkAsync_AdaptiveThreshold_UsedWhenFiveOrMoreSimilarities()
    {
        // 6 sentences = 5 similarity values, triggers adaptive 80th-percentile threshold
        var sentences = Enumerable.Range(1, 6)
            .Select(i => $"Sentence number {i} with content. ");
        var content = string.Join("", sentences);

        // Create embeddings where one pair is very dissimilar, rest are similar
        // This tests that the adaptive threshold splits at the right boundary
        SetupExplicitEmbeddings(new[]
        {
            new float[] { 1.0f, 0.0f, 0.0f },
            new float[] { 0.99f, 0.1f, 0.0f },
            new float[] { 0.98f, 0.2f, 0.0f },
            new float[] { 0.0f, 1.0f, 0.0f }, // Sharp topic change
            new float[] { 0.1f, 0.99f, 0.0f },
            new float[] { 0.2f, 0.98f, 0.0f }
        });

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 500, Overlap = 0, MinChunkSize = 1 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        // Should split at the sharp topic change
        result.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task ChunkAsync_SingleCharacterContent_ReturnsResult()
    {
        // Single character is not whitespace-only, but has no sentence boundaries
        var content = "X";
        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 500, Overlap = 0, MinChunkSize = 1 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        // Single "sentence" (no split markers), should return single chunk
        result.Should().ContainSingle();
        result[0].Content.Should().Be("X");
    }

    [Fact]
    public async Task ChunkAsync_BufferSize_AffectsEmbeddedTexts()
    {
        // Buffer size 0 = each sentence embedded alone; buffer size 1 = with neighbours.
        // We verify by capturing the texts passed to EmbedBatchAsync.
        var content = "First. Second. Third. ";

        IEnumerable<string>? capturedZero = null;
        _embeddingProvider.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedZero = ci.Arg<IEnumerable<string>>().ToArray();
                var texts = ((IEnumerable<string>)capturedZero).ToArray();
                return Task.FromResult<IReadOnlyList<float[]>>(
                    texts.Select((_, i) => new float[] { 1f, 0.1f * i, 0f }).ToList());
            });

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        await _chunker.ChunkAsync(parsedDoc, new ChunkingSettings
        {
            MaxChunkSize = 500, Overlap = 0, MinChunkSize = 1, SemanticBufferSize = 0
        });

        var bufferZeroTexts = capturedZero!.ToArray();

        IEnumerable<string>? capturedOne = null;
        _embeddingProvider.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedOne = ci.Arg<IEnumerable<string>>().ToArray();
                var texts = ((IEnumerable<string>)capturedOne).ToArray();
                return Task.FromResult<IReadOnlyList<float[]>>(
                    texts.Select((_, i) => new float[] { 1f, 0.1f * i, 0f }).ToList());
            });

        await _chunker.ChunkAsync(parsedDoc, new ChunkingSettings
        {
            MaxChunkSize = 500, Overlap = 0, MinChunkSize = 1, SemanticBufferSize = 1
        });

        var bufferOneTexts = capturedOne!.ToArray();

        // Buffer 0 should embed each sentence alone — middle text is just "Second."
        bufferZeroTexts[1].Should().Be("Second.");
        // Buffer 1 should embed middle sentence with neighbours — "First. Second. Third."
        bufferOneTexts[1].Should().Contain("First.").And.Contain("Second.").And.Contain("Third.");
        // The two configurations produce different embedded texts
        bufferZeroTexts.Should().NotBeEquivalentTo(bufferOneTexts);
    }

    [Fact]
    public async Task ChunkAsync_BreakpointMethod_StandardDeviation_ProducesValidChunks()
    {
        // 6 sentences -> 5 distances. Use mixed embeddings so std-dev calculation has variation.
        var content = "Alpha here. Beta here. Gamma here. Delta here. Epsilon here. Zeta here. ";
        SetupExplicitEmbeddings(new[]
        {
            new float[] { 1.0f, 0.0f, 0.0f },
            new float[] { 0.95f, 0.31f, 0.0f },
            new float[] { 0.90f, 0.43f, 0.0f },
            new float[] { 0.0f, 1.0f, 0.0f },
            new float[] { 0.31f, 0.95f, 0.0f },
            new float[] { 0.43f, 0.90f, 0.0f }
        });

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 500, Overlap = 0, MinChunkSize = 1,
            SemanticBreakpointMethod = "StandardDeviation",
            SemanticBreakpointAmount = 1.0
        };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        result.Should().NotBeEmpty();
        result.Should().OnlyContain(c => c.TokenCount > 0);
    }

    [Fact]
    public async Task ChunkAsync_BreakpointMethod_InterQuartile_ProducesValidChunks()
    {
        var content = "Alpha here. Beta here. Gamma here. Delta here. Epsilon here. Zeta here. ";
        SetupExplicitEmbeddings(new[]
        {
            new float[] { 1.0f, 0.0f, 0.0f },
            new float[] { 0.95f, 0.31f, 0.0f },
            new float[] { 0.90f, 0.43f, 0.0f },
            new float[] { 0.0f, 1.0f, 0.0f },
            new float[] { 0.31f, 0.95f, 0.0f },
            new float[] { 0.43f, 0.90f, 0.0f }
        });

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 500, Overlap = 0, MinChunkSize = 1,
            SemanticBreakpointMethod = "InterQuartile",
            SemanticBreakpointAmount = 1.5
        };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        result.Should().NotBeEmpty();
        result.Should().OnlyContain(c => c.TokenCount > 0);
    }

    [Fact]
    public async Task ChunkAsync_BreakpointMethod_Gradient_ProducesValidChunks()
    {
        var content = "Alpha here. Beta here. Gamma here. Delta here. Epsilon here. Zeta here. ";
        SetupExplicitEmbeddings(new[]
        {
            new float[] { 1.0f, 0.0f, 0.0f },
            new float[] { 0.95f, 0.31f, 0.0f },
            new float[] { 0.90f, 0.43f, 0.0f },
            new float[] { 0.0f, 1.0f, 0.0f },
            new float[] { 0.31f, 0.95f, 0.0f },
            new float[] { 0.43f, 0.90f, 0.0f }
        });

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 500, Overlap = 0, MinChunkSize = 1,
            SemanticBreakpointMethod = "Gradient",
            SemanticBreakpointAmount = 95
        };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        result.Should().NotBeEmpty();
        result.Should().OnlyContain(c => c.TokenCount > 0);
    }

    [Fact]
    public async Task ChunkAsync_BreakpointMethod_Gradient_SplitsAtGradientPeak_NotEveryHighDistance()
    {
        // Regression: ComputeBreakpointThreshold for "Gradient" returns a threshold
        // derived from the *gradient series* (forward-differences of distances), not
        // the distance series itself. The previous splits loop compared *distances*
        // against that gradient threshold — different units. On a smooth-distance
        // document with one step-up, the gradient threshold could be small while
        // every post-step distance value exceeded it, producing pathological
        // over-segmentation. The fix returns both threshold AND breakpoint array
        // and iterates the breakpoint array.
        //
        // Construction: 8 sentences arranged on a unit circle so adjacent cosine
        // similarities (and therefore distances) are precisely controlled. Distances
        // chosen so the *gradient* peaks uniquely at index 4 (the post-step value),
        // while distance values 4, 5, 6 are all "high". The fix produces a single
        // split at sentence boundary 4→5; the bug splits at 4→5, 5→6, 6→7.
        string content = "Sentence one body. Sentence two body. Sentence three body. " +
                         "Sentence four body. Sentence five body. Sentence six body. " +
                         "Sentence seven body. Sentence eight body.";

        // Target distances: gentle ramp [0.01, 0.02, 0.03, 0.04] then step to
        // [0.40, 0.60, 0.62]. Gradient peaks uniquely at index 4 (0.28).
        double[] targetDistances = new double[] { 0.01, 0.02, 0.03, 0.04, 0.40, 0.60, 0.62 };
        var embeddings = new float[8][];
        double cumulativeAngle = 0;
        embeddings[0] = new float[] { 1.0f, 0.0f, 0.0f };
        for (int i = 0; i < targetDistances.Length; i++)
        {
            // cos(delta) = 1 - distance[i]  =>  delta = acos(1 - distance[i])
            double cosSim = 1.0 - targetDistances[i];
            double deltaAngle = Math.Acos(cosSim);
            cumulativeAngle += deltaAngle;
            embeddings[i + 1] = new float[]
            {
                (float)Math.Cos(cumulativeAngle),
                (float)Math.Sin(cumulativeAngle),
                0.0f
            };
        }
        SetupExplicitEmbeddings(embeddings);

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 500, Overlap = 0, MinChunkSize = 1,
            // Default buffer 1 is fine — the chunker uses our explicit embeddings
            // verbatim regardless of the buffered texts it would otherwise generate.
            SemanticBreakpointMethod = "Gradient",
            SemanticBreakpointAmount = 95
        };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        // Expect exactly two chunks: sentences 1-5 and sentences 6-8.
        // (Pre-fix: 4 chunks, splitting at every "high" distance.)
        result.Should().HaveCount(2,
            "Gradient mode must iterate the gradient series, not distances — " +
            "a single gradient-peak should produce a single split, not one per high distance");
        result[0].Content.Should().Contain("Sentence one")
            .And.Contain("Sentence five");
        result[1].Content.Should().Contain("Sentence six")
            .And.Contain("Sentence eight");
    }

    [Fact]
    public async Task ChunkAsync_BreakpointMethod_PercentileHigh_ProducesFewerOrEqualChunksThanLow()
    {
        // Six sentences with several mid-range distances. Higher percentile = fewer splits.
        var content = "Alpha here. Beta here. Gamma here. Delta here. Epsilon here. Zeta here. ";
        var embeddings = new[]
        {
            new float[] { 1.0f, 0.0f, 0.0f },
            new float[] { 0.7f, 0.7f, 0.0f },
            new float[] { 0.0f, 1.0f, 0.0f },
            new float[] { 0.0f, 0.7f, 0.7f },
            new float[] { 0.0f, 0.0f, 1.0f },
            new float[] { 0.7f, 0.0f, 0.7f }
        };

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());

        SetupExplicitEmbeddings(embeddings);
        var lowResult = await _chunker.ChunkAsync(parsedDoc, new ChunkingSettings
        {
            MaxChunkSize = 500, Overlap = 0, MinChunkSize = 1,
            SemanticBreakpointMethod = "Percentile", SemanticBreakpointAmount = 50
        });

        SetupExplicitEmbeddings(embeddings);
        var highResult = await _chunker.ChunkAsync(parsedDoc, new ChunkingSettings
        {
            MaxChunkSize = 500, Overlap = 0, MinChunkSize = 1,
            SemanticBreakpointMethod = "Percentile", SemanticBreakpointAmount = 95
        });

        // Higher percentile = stricter threshold = fewer (or equal) splits.
        highResult.Count.Should().BeLessThanOrEqualTo(lowResult.Count);
    }

    [Fact]
    public async Task ChunkAsync_AllSentencesHighlySimilar_GroupsIntoSingleMeanPooledChunk()
    {
        // When all sentences are highly similar, the per-pair distances are all below the
        // threshold so no splits fire and the per-segment loop emits a SINGLE chunk that
        // mean-pools every sentence's embedding. Verifies the precomputed-embedding path
        // survives the refactor.
        var content = "Apple. Banana. Cherry. ";

        var emb1 = new float[] { 3.0f, 0.0f, 0.0f };
        var emb2 = new float[] { 3.0f, 0.001f, 0.0f }; // almost identical
        var emb3 = new float[] { 3.0f, 0.0f, 0.001f };
        SetupExplicitEmbeddings(new[] { emb1, emb2, emb3 });

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 500, Overlap = 0, MinChunkSize = 50 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        result.Should().ContainSingle();
        result[0].Content.Should().Contain("Apple").And.Contain("Banana").And.Contain("Cherry");
        // Single pre-merge chunk -> mean-pooled embedding survives (merge-forward only fires
        // when there are 2+ raw chunks).
        float[]? embedding = result[0].PrecomputedEmbedding;
        embedding.Should().NotBeNull();
        embedding![0].Should().BeApproximately(3.0f, 0.01f);
    }

    [Fact]
    public async Task ChunkAsync_OffsetsRoundTripWithSourceText()
    {
        // Multi-sentence document — every emitted chunk's recorded offsets must point
        // at a substring of `content` that, after Trim(), exactly equals the chunk's Content.
        // This regression-tests the IndexOf-from-currentOffset bug: when a sentence is
        // re-emitted slightly differently from a verbatim source slice (e.g., trailing
        // whitespace stripped by Trim()), IndexOf used to return -1 and we silently
        // fell back to the previous offset, producing drift. The hint-based search fixes it.
        var content = "First sentence about cats. Second sentence about dogs. " +
                      "Third sentence about birds. Fourth sentence about fish. " +
                      "Fifth sentence about reptiles. Sixth sentence about insects. " +
                      "Seventh sentence about mammals. Eighth sentence about amphibians.";
        SetupEmbeddings(8, highSimilarity: false);

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 500, Overlap = 0, MinChunkSize = 1 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        result.Should().NotBeEmpty();
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
    public async Task ChunkAsync_DoesNotSilentlyDropContentBelowMinChunkSize()
    {
        // Three sentences: small "marker" sentence sandwiched between two large ones.
        // Embeddings are arranged so the segmenter always splits at every boundary
        // (orthogonal vectors -> high distance everywhere). Pre-merge we get three
        // chunks; the small "marker" one is below MinChunkSize and used to be
        // silently dropped. With merge-forward it must be merged into a neighbour.
        string longA = string.Join(" ", Enumerable.Repeat("alpha", 30)) + ".";
        string tinyB = "marker.";
        string longC = string.Join(" ", Enumerable.Repeat("charlie", 30)) + ".";
        string content = longA + " " + tinyB + " " + longC;

        SetupExplicitEmbeddings(new[]
        {
            new float[] { 1.0f, 0.0f, 0.0f },
            new float[] { 0.0f, 1.0f, 0.0f },
            new float[] { 0.0f, 0.0f, 1.0f }
        });

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 500, Overlap = 0, MinChunkSize = 5 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        bool markerPresent = result.Any(c => c.Content.Contains("marker"));
        markerPresent.Should().BeTrue(
            "MinChunkSize must not silently drop document content; " +
            "small segments must be merged into a neighbour, not discarded");
    }
}
