using Connapse.Core;
using Connapse.Search.Hybrid;
using FluentAssertions;

namespace Connapse.Core.Tests.Search;

/// <summary>
/// Tests for the built-in RRF fusion step inside HybridSearchService.
/// These test HybridSearchService.FuseResults directly — the internal static method
/// that merges vector and keyword hits with deduplication, source tagging, and score normalization.
///
/// All tests set Reranker = "None" conceptually (RRF fusion is built into HybridSearchService;
/// we are testing that internal fusion logic directly).
/// </summary>
[Trait("Category", "Unit")]
public class HybridSearchFusionTests
{
    private const int DefaultRrfK = 60;

    [Fact]
    public void BothSourcesReturnResults_FusesAndDeduplicates()
    {
        // Vector returns [c1, c2, c3], keyword returns [c2, c4, c5]
        var vectorHits = new List<SearchHit>
        {
            Hit("c1", "doc1", 0.9f, "vector"),
            Hit("c2", "doc2", 0.8f, "vector"),
            Hit("c3", "doc3", 0.7f, "vector")
        };
        var keywordHits = new List<SearchHit>
        {
            Hit("c2", "doc2", 0.95f, "keyword"),
            Hit("c4", "doc4", 0.85f, "keyword"),
            Hit("c5", "doc5", 0.75f, "keyword")
        };

        var result = HybridSearchService.FuseResults(vectorHits, keywordHits, DefaultRrfK);

        // Should have 5 unique chunks (c2 deduplicated)
        result.Should().HaveCount(5);
        result.Select(h => h.ChunkId).Should().OnlyHaveUniqueItems();

        // c2 appears in both lists so should have the highest RRF score and be first
        result.First().ChunkId.Should().Be("c2");

        // c2's source should indicate it came from both
        result.First().Metadata["source"].Should().Be("both");
    }

    [Fact]
    public void VectorReturnsEmpty_ReturnsKeywordOnly()
    {
        var vectorHits = new List<SearchHit>();
        var keywordHits = new List<SearchHit>
        {
            Hit("c1", "doc1", 0.9f, "keyword"),
            Hit("c2", "doc2", 0.8f, "keyword")
        };

        var result = HybridSearchService.FuseResults(vectorHits, keywordHits, DefaultRrfK);

        result.Should().HaveCount(2);
        result.Should().OnlyContain(h => h.Metadata["source"] == "keyword");
    }

    [Fact]
    public void KeywordReturnsEmpty_ReturnsVectorOnly()
    {
        var vectorHits = new List<SearchHit>
        {
            Hit("c1", "doc1", 0.9f, "vector"),
            Hit("c2", "doc2", 0.8f, "vector")
        };
        var keywordHits = new List<SearchHit>();

        var result = HybridSearchService.FuseResults(vectorHits, keywordHits, DefaultRrfK);

        result.Should().HaveCount(2);
        result.Should().OnlyContain(h => h.Metadata["source"] == "vector");
    }

    [Fact]
    public void DisjointResults_AllIncluded()
    {
        // No overlap between vector and keyword results
        var vectorHits = new List<SearchHit>
        {
            Hit("c1", "doc1", 0.9f, "vector"),
            Hit("c2", "doc2", 0.8f, "vector")
        };
        var keywordHits = new List<SearchHit>
        {
            Hit("c3", "doc3", 0.85f, "keyword"),
            Hit("c4", "doc4", 0.75f, "keyword")
        };

        var result = HybridSearchService.FuseResults(vectorHits, keywordHits, DefaultRrfK);

        result.Should().HaveCount(4);
        result.Select(h => h.ChunkId).Should().BeEquivalentTo(["c1", "c2", "c3", "c4"]);

        // Each result should be tagged with its original source (not "both")
        result.Should().OnlyContain(h =>
            h.Metadata["source"] == "vector" || h.Metadata["source"] == "keyword");
        result.Should().NotContain(h => h.Metadata["source"] == "both");
    }

    [Fact]
    public void OverlappingChunk_TaggedBoth()
    {
        // Single chunk appears in both lists
        var vectorHits = new List<SearchHit>
        {
            Hit("c1", "doc1", 0.9f, "vector")
        };
        var keywordHits = new List<SearchHit>
        {
            Hit("c1", "doc1", 0.85f, "keyword")
        };

        var result = HybridSearchService.FuseResults(vectorHits, keywordHits, DefaultRrfK);

        result.Should().HaveCount(1);
        result.Single().ChunkId.Should().Be("c1");
        result.Single().Metadata["source"].Should().Be("both");
    }

    [Fact]
    public void ScoresNormalizedTo01()
    {
        var vectorHits = new List<SearchHit>
        {
            Hit("c1", "doc1", 0.9f, "vector"),
            Hit("c2", "doc2", 0.8f, "vector"),
            Hit("c3", "doc3", 0.7f, "vector")
        };
        var keywordHits = new List<SearchHit>
        {
            Hit("c4", "doc4", 0.95f, "keyword"),
            Hit("c5", "doc5", 0.85f, "keyword")
        };

        var result = HybridSearchService.FuseResults(vectorHits, keywordHits, DefaultRrfK);

        // All scores should be in [0, 1]
        result.Should().OnlyContain(h => h.Score >= 0f && h.Score <= 1f);

        // The highest score should be normalized to exactly 1.0
        result.Max(h => h.Score).Should().Be(1.0f);
    }

    [Fact]
    public void ResultsSortedByScoreDescending()
    {
        var vectorHits = new List<SearchHit>
        {
            Hit("c1", "doc1", 0.9f, "vector"),
            Hit("c2", "doc2", 0.5f, "vector"),
            Hit("c3", "doc3", 0.3f, "vector")
        };
        var keywordHits = new List<SearchHit>
        {
            Hit("c4", "doc4", 0.8f, "keyword"),
            Hit("c5", "doc5", 0.6f, "keyword")
        };

        var result = HybridSearchService.FuseResults(vectorHits, keywordHits, DefaultRrfK);

        for (var i = 1; i < result.Count; i++)
        {
            result[i - 1].Score.Should().BeGreaterThanOrEqualTo(result[i].Score,
                $"result[{i - 1}] (score={result[i - 1].Score}) should be >= result[{i}] (score={result[i].Score})");
        }
    }

    /// <summary>
    /// Helper to create a SearchHit with source metadata.
    /// </summary>
    private static SearchHit Hit(string chunkId, string documentId, float score, string source)
    {
        return new SearchHit(
            ChunkId: chunkId,
            DocumentId: documentId,
            Content: $"Content for {chunkId}",
            Score: score,
            Metadata: new Dictionary<string, string> { ["source"] = source });
    }
}
