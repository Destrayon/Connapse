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
        _chunker = new SemanticChunker(_embeddingProvider, new TiktokenTokenCounter(), new PragmaticSentenceSegmenter());
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
    public async Task ChunkAsync_AllChunksBelowMinSize_ReturnsFallbackWholeContent()
    {
        // Three short sentences — Pragmatic segmenter splits these into 3, but each is too
        // small to satisfy MinChunkSize, so the fallback path returns the whole content.
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

        // Safety net: returns whole content as one chunk when everything is filtered
        result.Should().ContainSingle();
        result[0].Content.Should().Be(content.Trim());
        result[0].ChunkIndex.Should().Be(0);
        result[0].PrecomputedEmbedding.Should().NotBeNull();
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
    public async Task ChunkAsync_FallbackChunk_HasMeanPooledEmbedding()
    {
        // All chunks filtered by MinChunkSize -> fallback returns whole content with mean-pooled embedding
        var content = "Apple. Banana. Cherry. ";

        var emb1 = new float[] { 3.0f, 0.0f, 0.0f };
        var emb2 = new float[] { 0.0f, 6.0f, 0.0f };
        var emb3 = new float[] { 0.0f, 0.0f, 9.0f };
        SetupExplicitEmbeddings(new[] { emb1, emb2, emb3 });

        var parsedDoc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 500, Overlap = 0, MinChunkSize = 50 };

        var result = await _chunker.ChunkAsync(parsedDoc, settings);

        result.Should().ContainSingle();
        var embedding = result[0].PrecomputedEmbedding;
        embedding.Should().NotBeNull();
        // Mean of [3,0,0], [0,6,0], [0,0,9] = [1,2,3]
        embedding![0].Should().BeApproximately(1.0f, 0.01f);
        embedding[1].Should().BeApproximately(2.0f, 0.01f);
        embedding[2].Should().BeApproximately(3.0f, 0.01f);
    }
}
