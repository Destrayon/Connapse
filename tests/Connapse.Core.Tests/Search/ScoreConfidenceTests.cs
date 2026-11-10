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

    private static readonly NoMatchCutoff Nomic = new(0.69f, 0.61f);

    [Theory]
    [InlineData(0.79f)]
    [InlineData(0.3f)]
    public void ScaleScores_AnyFactor_KeepsTheOrderAndAddsNoTies(float factor)
    {
        List<SearchHit> hits = [Hit("a", 1f), Hit("b", 0.97f), Hit("c", 0.5f), Hit("d", 0.01f)];

        List<SearchHit> scaled = HybridSearchService.ScaleScores(hits, factor);

        scaled.OrderByDescending(h => h.Score).Select(h => h.ChunkId).Should().Equal("a", "b", "c", "d");
        scaled.Select(h => h.Score).Should().OnlyHaveUniqueItems();
        scaled.Should().OnlyContain(h => h.Score <= 1f);
    }

    [Theory]
    [InlineData(0.95f, 1f)]
    [InlineData(0.69f, 1f)]
    [InlineData(0.6899f, 0.79f)]
    [InlineData(0.65f, 0.545f)]
    [InlineData(0.61f, 0.3f)]
    [InlineData(0.2f, 0.3f)]
    public void Factor_BestCosine_OneAtTheCutoffThenFallsToTheFloor(float best, float expected)
    {
        ScoreConfidence.Factor(best, Nomic).Should().BeApproximately(expected, 0.002f);
    }

    [Fact]
    public void NoMatchFactor_KeywordSearch_LeavesScoresAlone()
    {
        HybridSearchService.NoMatchFactor([Hit("a", 0.1f)], SearchMode.Keyword, Nomic).Should().Be(1f);
    }

    [Fact]
    public void NoMatchFactor_HybridHitsWithoutCosines_LeavesScoresAlone()
    {
        HybridSearchService.NoMatchFactor([Hit("a", 1f)], SearchMode.Hybrid, Nomic).Should().Be(1f);
    }

    [Fact]
    public void NoMatchFactor_ModelWithoutACutoff_LeavesScoresAlone()
    {
        HybridSearchService.NoMatchFactor([Hit("a", 0.1f)], SearchMode.Semantic, null).Should().Be(1f);
    }

    [Fact]
    public void NoMatchFactor_HybridSearch_ReadsTheBestRecordedCosineNotTheFusedScores()
    {
        List<SearchHit> fused = HybridSearchService.FuseResults(
            [Hit("a", 0.65f), Hit("b", 0.4f)], [Hit("b", 9f)], 0.65f);

        HybridSearchService.NoMatchFactor(fused, SearchMode.Hybrid, Nomic)
            .Should().Be(ScoreConfidence.Factor(0.65f, Nomic));
    }

    [Fact]
    public void NoMatchFactor_SemanticSearchAboveTheCutoff_LeavesScoresAlone()
    {
        HybridSearchService.NoMatchFactor([Hit("a", 0.4f), Hit("b", 0.72f)], SearchMode.Semantic, Nomic).Should().Be(1f);
    }

    [Theory]
    [InlineData("nomic-embed-text")]
    [InlineData("nomic-embed-text:latest")]
    [InlineData("NOMIC-EMBED-TEXT")]
    public void CutoffFor_BuiltInModel_UsesItsCutoff(string model)
    {
        ScoreConfidence.CutoffFor(new EmbeddingSettings { Model = model }, new SearchSettings()).Should().Be(Nomic);
    }

    [Fact]
    public void CutoffFor_UnknownModel_HasNone()
    {
        ScoreConfidence.CutoffFor(new EmbeddingSettings { Model = "text-embedding-3-small" }, new SearchSettings())
            .Should().BeNull();
    }

    [Fact]
    public void CutoffFor_CustomPrefixes_HasNone()
    {
        EmbeddingSettings embedding = new() { Model = "nomic-embed-text", UseModelPrefixes = false };

        ScoreConfidence.CutoffFor(embedding, new SearchSettings()).Should().BeNull();
    }

    [Fact]
    public void CutoffFor_ConfiguredCutoff_OverridesAndDefaultsTheFloor()
    {
        ScoreConfidence.CutoffFor(new EmbeddingSettings { Model = "text-embedding-3-small" }, new SearchSettings { NoMatchCutoff = 0.4 })
            .Should().Be(new NoMatchCutoff(0.4f, 0.32f));
        ScoreConfidence.CutoffFor(new EmbeddingSettings(), new SearchSettings { NoMatchCutoff = 0.4, NoMatchFloor = 0.2 })
            .Should().Be(new NoMatchCutoff(0.4f, 0.2f));
    }

    [Fact]
    public void CutoffFor_ZeroCutoff_TurnsTheFlagOff()
    {
        ScoreConfidence.CutoffFor(new EmbeddingSettings { Model = "nomic-embed-text" }, new SearchSettings { NoMatchCutoff = 0 })
            .Should().BeNull();
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
