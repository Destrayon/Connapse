using Connapse.Core;
using Connapse.Search.Hybrid;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Core.Tests.Search;

/// <summary>
/// Search scores that mean the same from one query to the next, and the no-match flag (CON-278).
/// </summary>
[Trait("Category", "Unit")]
public class ScoreConfidenceTests
{
    [Fact]
    public void FuseResults_HitFoundByBothSearches_CarriesBothRawScores()
    {
        List<SearchHit> vector = [Hit("c1", 0.71f), Hit("c2", 0.52f)];
        List<SearchHit> keyword = [Hit("c1", 7.5f), Hit("c3", 2.25f)];

        List<SearchHit> fused = HybridSearchService.FuseResults(vector, keyword, 0.65f);

        SearchHit c1 = fused.Single(h => h.ChunkId == "c1");
        c1.Metadata[ScoreConfidence.RawVectorKey].Should().Be("0.7100");
        c1.Metadata[ScoreConfidence.RawKeywordKey].Should().Be("7.5000");
        fused.Single(h => h.ChunkId == "c2").Metadata.Should().NotContainKey(ScoreConfidence.RawKeywordKey);
        fused.Single(h => h.ChunkId == "c3").Metadata.Should().NotContainKey(ScoreConfidence.RawVectorKey);
    }

    [Fact]
    public void FuseResultsDbsf_Hit_CarriesItsRawCosine()
    {
        List<SearchHit> fused = HybridSearchService.FuseResultsDbsf([Hit("c1", 0.64f), Hit("c2", 0.5f)], [], 0.65f);

        fused.Single(h => h.ChunkId == "c1").Metadata[ScoreConfidence.RawVectorKey].Should().Be("0.6400");
    }

    [Theory]
    [InlineData(0.9f)]
    [InlineData(0.2f)]
    [InlineData(0f)]
    public void ScaleScores_AnyConfidence_KeepsTheOrderAndAddsNoTies(float confidence)
    {
        List<SearchHit> hits = [Hit("a", 1f), Hit("b", 0.97f), Hit("c", 0.5f), Hit("d", 0.01f)];

        List<SearchHit> scaled = HybridSearchService.ScaleScores(hits, confidence);

        scaled.OrderByDescending(h => h.Score).Select(h => h.ChunkId).Should().Equal("a", "b", "c", "d");
        scaled.Select(h => h.Score).Should().OnlyHaveUniqueItems();
        scaled.Should().OnlyContain(h => h.Score <= 1f);
    }

    [Fact]
    public void ConfidenceOf_KeywordSearch_IsUnknown()
    {
        HybridSearchService.ConfidenceOf([Hit("a", 12f)], SearchMode.Keyword).Should().BeNull();
    }

    [Fact]
    public void ConfidenceOf_HybridHitsWithoutCosines_IsUnknown()
    {
        HybridSearchService.ConfidenceOf([Hit("a", 1f)], SearchMode.Hybrid).Should().BeNull();
    }

    [Fact]
    public void ConfidenceOf_HybridSearch_ReadsTheRecordedCosinesNotTheFusedScores()
    {
        List<SearchHit> fused = HybridSearchService.FuseResults(
            [Hit("a", 0.8f), Hit("b", 0.4f), Hit("c", 0.38f)], [], 0.65f);

        HybridSearchService.ConfidenceOf(fused, SearchMode.Hybrid)
            .Should().Be(HybridSearchService.ConfidenceOf([Hit("a", 0.8f), Hit("b", 0.4f), Hit("c", 0.38f)], SearchMode.Semantic));
    }

    [Fact]
    public void Of_StandoutCloseHit_IsMoreConfidentThanAFlatDistantField()
    {
        float? answered = ScoreConfidence.Of([Hit("a", 0.82f), Hit("b", 0.55f), Hit("c", 0.52f)], h => h.Score);
        float? unanswered = ScoreConfidence.Of([Hit("a", 0.48f), Hit("b", 0.47f), Hit("c", 0.47f)], h => h.Score);

        answered.Should().BeGreaterThan(unanswered!.Value);
        answered.Should().BeGreaterThanOrEqualTo(ScoreConfidence.NoMatchThreshold);
        unanswered.Should().BeLessThan(ScoreConfidence.NoMatchThreshold);
    }

    [Fact]
    public void Of_OnlyTheTopWindowCounts()
    {
        List<SearchHit> top = Enumerable.Range(0, ScoreConfidence.Window).Select(i => Hit($"t{i}", 0.7f - i * 0.01f)).ToList();
        List<SearchHit> withTail = [.. top, .. Enumerable.Range(0, 20).Select(i => Hit($"x{i}", 0.1f))];

        ScoreConfidence.Of(withTail, h => h.Score).Should().Be(ScoreConfidence.Of(top, h => h.Score));
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_SaysNothingMatched()
    {
        IOptionsMonitor<SearchSettings> settings = Substitute.For<IOptionsMonitor<SearchSettings>>();
        settings.CurrentValue.Returns(new SearchSettings());
        HybridSearchService service = new(
            Substitute.For<IServiceScopeFactory>(), [], settings, NullLogger<HybridSearchService>.Instance);

        SearchResult result = await service.SearchAsync(" ", new SearchOptions());

        result.NoGoodMatch.Should().BeTrue();
    }

    private static SearchHit Hit(string chunkId, float score) =>
        new(chunkId, "doc-" + chunkId, "content", score, new Dictionary<string, string>());
}
