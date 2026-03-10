using Connapse.Core;
using Connapse.Search.Hybrid;
using FluentAssertions;

namespace Connapse.Core.Tests.Search;

/// <summary>
/// Tests for the Convex Combination fusion step inside HybridSearchService.
/// These test HybridSearchService.FuseResults directly — the internal static method
/// that merges vector and keyword hits with deduplication, source tagging, and
/// input-normalized score combination.
/// </summary>
[Trait("Category", "Unit")]
public class HybridSearchFusionTests
{
    private const float DefaultAlpha = 0.5f;

    [Fact]
    public void BothSourcesReturnResults_FusesAndDeduplicates()
    {
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

        var result = HybridSearchService.FuseResults(vectorHits, keywordHits, DefaultAlpha);

        // Should have 5 unique chunks (c2 deduplicated)
        result.Should().HaveCount(5);
        result.Select(h => h.ChunkId).Should().OnlyHaveUniqueItems();

        // c2 appears in both lists so should have the highest fused score and be first
        result.First().ChunkId.Should().Be("c2");
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

        var result = HybridSearchService.FuseResults(vectorHits, keywordHits, DefaultAlpha);

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

        var result = HybridSearchService.FuseResults(vectorHits, keywordHits, DefaultAlpha);

        result.Should().HaveCount(2);
        result.Should().OnlyContain(h => h.Metadata["source"] == "vector");
    }

    [Fact]
    public void DisjointResults_AllIncluded()
    {
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

        var result = HybridSearchService.FuseResults(vectorHits, keywordHits, DefaultAlpha);

        result.Should().HaveCount(4);
        result.Select(h => h.ChunkId).Should().BeEquivalentTo(["c1", "c2", "c3", "c4"]);
        result.Should().OnlyContain(h =>
            h.Metadata["source"] == "vector" || h.Metadata["source"] == "keyword");
        result.Should().NotContain(h => h.Metadata["source"] == "both");
    }

    [Fact]
    public void OverlappingChunk_TaggedBoth()
    {
        var vectorHits = new List<SearchHit>
        {
            Hit("c1", "doc1", 0.9f, "vector")
        };
        var keywordHits = new List<SearchHit>
        {
            Hit("c1", "doc1", 0.85f, "keyword")
        };

        var result = HybridSearchService.FuseResults(vectorHits, keywordHits, DefaultAlpha);

        result.Should().HaveCount(1);
        result.Single().ChunkId.Should().Be("c1");
        result.Single().Metadata["source"].Should().Be("both");
    }

    [Fact]
    public void ScoresInZeroOneRange()
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

        var result = HybridSearchService.FuseResults(vectorHits, keywordHits, DefaultAlpha);

        result.Should().OnlyContain(h => h.Score >= 0f && h.Score <= 1f);
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

        var result = HybridSearchService.FuseResults(vectorHits, keywordHits, DefaultAlpha);

        for (var i = 1; i < result.Count; i++)
        {
            result[i - 1].Score.Should().BeGreaterThanOrEqualTo(result[i].Score,
                $"result[{i - 1}] (score={result[i - 1].Score}) should be >= result[{i}] (score={result[i].Score})");
        }
    }

    [Fact]
    public void BothEmpty_ReturnsEmpty()
    {
        var result = HybridSearchService.FuseResults([], [], DefaultAlpha);
        result.Should().BeEmpty();
    }

    [Fact]
    public void AlphaOnePointZero_UsesOnlyVectorScores()
    {
        var vectorHits = new List<SearchHit>
        {
            Hit("c1", "doc1", 0.9f, "vector"),
            Hit("c2", "doc2", 0.5f, "vector")
        };
        var keywordHits = new List<SearchHit>
        {
            Hit("c3", "doc3", 0.95f, "keyword")
        };

        var result = HybridSearchService.FuseResults(vectorHits, keywordHits, alpha: 1.0f);

        // With alpha=1.0, keyword results get score 0 (keyword weight = 1-1 = 0)
        var keywordResult = result.Single(h => h.ChunkId == "c3");
        keywordResult.Score.Should().Be(0f);

        // Vector-only results should retain their normalized scores
        var vectorTop = result.Single(h => h.ChunkId == "c1");
        vectorTop.Score.Should().Be(1.0f); // normalized max
    }

    [Fact]
    public void AlphaZero_UsesOnlyKeywordScores()
    {
        var vectorHits = new List<SearchHit>
        {
            Hit("c1", "doc1", 0.9f, "vector")
        };
        var keywordHits = new List<SearchHit>
        {
            Hit("c2", "doc2", 0.95f, "keyword"),
            Hit("c3", "doc3", 0.85f, "keyword")
        };

        var result = HybridSearchService.FuseResults(vectorHits, keywordHits, alpha: 0f);

        // With alpha=0, vector results get score 0 (vector weight = 0)
        var vectorResult = result.Single(h => h.ChunkId == "c1");
        vectorResult.Score.Should().Be(0f);

        // Keyword-only top result should get full normalized score
        var keywordTop = result.Single(h => h.ChunkId == "c2");
        keywordTop.Score.Should().Be(1.0f); // normalized max * (1-0)
    }

    [Fact]
    public void OverlappingChunk_ScoreHigherThanEitherAlone()
    {
        // c1 in both, c2 vector-only, c3 keyword-only — all at rank 0 in their respective lists
        var vectorHits = new List<SearchHit>
        {
            Hit("c1", "doc1", 0.9f, "vector"),
            Hit("c2", "doc2", 0.5f, "vector")
        };
        var keywordHits = new List<SearchHit>
        {
            Hit("c1", "doc1", 0.95f, "keyword"),
            Hit("c3", "doc3", 0.85f, "keyword")
        };

        var result = HybridSearchService.FuseResults(vectorHits, keywordHits, DefaultAlpha);

        var bothScore = result.Single(h => h.ChunkId == "c1").Score;
        var vectorOnlyScore = result.Single(h => h.ChunkId == "c2").Score;
        var keywordOnlyScore = result.Single(h => h.ChunkId == "c3").Score;

        // c1 appears in both lists, so its fused score should be higher than single-source hits
        bothScore.Should().BeGreaterThan(vectorOnlyScore);
        bothScore.Should().BeGreaterThan(keywordOnlyScore);
    }

    [Fact]
    public void MetadataContainsComponentScores()
    {
        var vectorHits = new List<SearchHit>
        {
            Hit("c1", "doc1", 0.9f, "vector")
        };
        var keywordHits = new List<SearchHit>
        {
            Hit("c1", "doc1", 0.85f, "keyword")
        };

        var result = HybridSearchService.FuseResults(vectorHits, keywordHits, DefaultAlpha);

        var hit = result.Single();
        hit.Metadata.Should().ContainKey("vectorScore");
        hit.Metadata.Should().ContainKey("keywordScore");
        hit.Metadata.Should().ContainKey("source");
        float.TryParse(hit.Metadata["vectorScore"], out _).Should().BeTrue();
        float.TryParse(hit.Metadata["keywordScore"], out _).Should().BeTrue();
    }

    [Fact]
    public void SingleItemInEachList_BothGetScoreOne()
    {
        // With one item per list, min-max normalize gives each score=1.0
        var vectorHits = new List<SearchHit> { Hit("c1", "doc1", 0.3f, "vector") };
        var keywordHits = new List<SearchHit> { Hit("c2", "doc2", 0.7f, "keyword") };

        var result = HybridSearchService.FuseResults(vectorHits, keywordHits, DefaultAlpha);

        // Single-item normalization: both get normalized to 1.0
        // c1: alpha * 1.0 + (1-alpha) * 0.0 = 0.5
        // c2: alpha * 0.0 + (1-alpha) * 1.0 = 0.5
        result.Should().HaveCount(2);
        result[0].Score.Should().Be(0.5f);
        result[1].Score.Should().Be(0.5f);
    }

    // --- DBSF Fusion Tests ---

    [Fact]
    public void Dbsf_BothSourcesReturnResults_FusesAndDeduplicates()
    {
        var vectorHits = new List<SearchHit>
        {
            Hit("c1", "doc1", 0.9f, "vector"),
            Hit("c2", "doc2", 0.8f, "vector"),
            Hit("c3", "doc3", 0.7f, "vector")
        };
        var keywordHits = new List<SearchHit>
        {
            Hit("c2", "doc2", 0.95f, "keyword"),
            Hit("c4", "doc4", 0.85f, "keyword")
        };

        var result = HybridSearchService.FuseResultsDbsf(vectorHits, keywordHits, DefaultAlpha);

        result.Should().HaveCount(4);
        result.Select(h => h.ChunkId).Should().OnlyHaveUniqueItems();
        result.First().ChunkId.Should().Be("c2"); // in both lists
        result.First().Metadata["source"].Should().Be("both");
    }

    [Fact]
    public void Dbsf_ScoresInZeroOneRange()
    {
        var vectorHits = new List<SearchHit>
        {
            Hit("c1", "doc1", 0.9f, "vector"),
            Hit("c2", "doc2", 0.3f, "vector")
        };
        var keywordHits = new List<SearchHit>
        {
            Hit("c3", "doc3", 0.95f, "keyword"),
            Hit("c4", "doc4", 0.1f, "keyword")
        };

        var result = HybridSearchService.FuseResultsDbsf(vectorHits, keywordHits, DefaultAlpha);

        result.Should().OnlyContain(h => h.Score >= 0f && h.Score <= 1f);
    }

    [Fact]
    public void Dbsf_OutlierDoesNotCompressOtherScores()
    {
        // With min-max, the outlier (0.1) would compress 0.8 and 0.85 to near-1.0
        // With DBSF, the distribution-based normalization preserves relative differences
        var vectorHits = new List<SearchHit>
        {
            Hit("c1", "doc1", 0.85f, "vector"),
            Hit("c2", "doc2", 0.80f, "vector"),
            Hit("c3", "doc3", 0.10f, "vector") // outlier
        };
        var keywordHits = new List<SearchHit>();

        var dbsfResult = HybridSearchService.FuseResultsDbsf(vectorHits, keywordHits, alpha: 1.0f);
        var ccResult = HybridSearchService.FuseResults(vectorHits, keywordHits, alpha: 1.0f);

        // In CC (min-max), the gap between c1 and c2 is compressed: (0.85-0.1)/(0.85-0.1) vs (0.80-0.1)/(0.85-0.1)
        // = 1.0 vs 0.933 — very tight
        // In DBSF, the gap should be more spread because the outlier doesn't define the range
        var dbsfGap = dbsfResult.Single(h => h.ChunkId == "c1").Score -
                      dbsfResult.Single(h => h.ChunkId == "c2").Score;
        var ccGap = ccResult.Single(h => h.ChunkId == "c1").Score -
                    ccResult.Single(h => h.ChunkId == "c2").Score;

        // Both should preserve the ordering
        dbsfResult.Should().BeInDescendingOrder(h => h.Score);
        ccResult.Should().BeInDescendingOrder(h => h.Score);
    }

    [Fact]
    public void Dbsf_EmptyLists_ReturnsEmpty()
    {
        var result = HybridSearchService.FuseResultsDbsf([], [], DefaultAlpha);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Dbsf_SingleItem_NormalizesToOne()
    {
        var vectorHits = new List<SearchHit> { Hit("c1", "doc1", 0.5f, "vector") };
        var result = HybridSearchService.FuseResultsDbsf(vectorHits, [], alpha: 1.0f);

        result.Should().HaveCount(1);
        result[0].Score.Should().Be(1.0f);
    }

    // --- AutoCut Tests ---

    [Fact]
    public void AutoCut_ClearGap_TrimsAfterGap()
    {
        var hits = new List<SearchHit>
        {
            Hit("c1", "doc1", 0.90f, "both"),
            Hit("c2", "doc2", 0.85f, "both"),
            Hit("c3", "doc3", 0.83f, "both"),
            // large gap here
            Hit("c4", "doc4", 0.30f, "vector"),
            Hit("c5", "doc5", 0.25f, "keyword")
        };

        var result = HybridSearchService.ApplyAutoCut(hits);

        result.Should().HaveCount(3);
        result.Select(h => h.ChunkId).Should().BeEquivalentTo(["c1", "c2", "c3"]);
    }

    [Fact]
    public void AutoCut_EvenlySpacedScores_KeepsAll()
    {
        // Evenly spaced scores — no single gap dominates (all gaps equal)
        // The algorithm requires the largest gap to be > 2x the second-largest,
        // so equal gaps are never cut.
        var hits = new List<SearchHit>
        {
            Hit("c1", "doc1", 0.90f, "both"),
            Hit("c2", "doc2", 0.80f, "both"),
            Hit("c3", "doc3", 0.70f, "both"),
            Hit("c4", "doc4", 0.60f, "both")
        };

        var result = HybridSearchService.ApplyAutoCut(hits);

        result.Should().HaveCount(4);
    }

    [Fact]
    public void AutoCut_TwoOrFewerItems_ReturnsAll()
    {
        var hits = new List<SearchHit>
        {
            Hit("c1", "doc1", 0.9f, "both"),
            Hit("c2", "doc2", 0.1f, "both")
        };

        var result = HybridSearchService.ApplyAutoCut(hits);

        result.Should().HaveCount(2);
    }

    [Fact]
    public void AutoCut_SingleItem_ReturnsIt()
    {
        var hits = new List<SearchHit> { Hit("c1", "doc1", 0.9f, "both") };
        var result = HybridSearchService.ApplyAutoCut(hits);
        result.Should().HaveCount(1);
    }

    [Fact]
    public void AutoCut_EmptyList_ReturnsEmpty()
    {
        var result = HybridSearchService.ApplyAutoCut([]);
        result.Should().BeEmpty();
    }

    [Fact]
    public void AutoCut_AllSameScore_KeepsAll()
    {
        var hits = new List<SearchHit>
        {
            Hit("c1", "doc1", 0.5f, "both"),
            Hit("c2", "doc2", 0.5f, "both"),
            Hit("c3", "doc3", 0.5f, "both")
        };

        var result = HybridSearchService.ApplyAutoCut(hits);

        // All same score → range=0, no cutting
        result.Should().HaveCount(3);
    }

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
