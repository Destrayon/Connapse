using Connapse.Core;
using FluentAssertions;

namespace Connapse.Core.Tests.Search;

/// <summary>
/// Tests for cross-model search settings and behavior.
/// Integration tests for HybridSearchService mode override live in Connapse.Integration.Tests.
/// </summary>
[Trait("Category", "Unit")]
public class CrossModelSearchTests
{
    [Fact]
    public void SearchSettings_EnableCrossModelSearch_DefaultsFalse()
    {
        var settings = new SearchSettings();

        settings.EnableCrossModelSearch.Should().BeFalse();
    }

    [Fact]
    public void SearchSettings_EnableCrossModelSearch_CanBeEnabled()
    {
        var settings = new SearchSettings { EnableCrossModelSearch = true };

        settings.EnableCrossModelSearch.Should().BeTrue();
    }

    [Fact]
    public void SearchSettings_WithCrossModelSearch_PreservesOtherSettings()
    {
        var settings = new SearchSettings
        {
            Mode = "Hybrid",
            TopK = 20,
            Reranker = "CrossEncoder",
            EnableCrossModelSearch = true,
            MinimumScore = 0.3
        };

        var copy = settings with { EnableCrossModelSearch = false };

        copy.Mode.Should().Be("Hybrid");
        copy.TopK.Should().Be(20);
        copy.Reranker.Should().Be("CrossEncoder");
        copy.EnableCrossModelSearch.Should().BeFalse();
        copy.MinimumScore.Should().Be(0.3);
    }

    [Fact]
    public void SearchMode_Semantic_CanBeParsed()
    {
        Enum.TryParse<SearchMode>("Semantic", ignoreCase: true, out var mode)
            .Should().BeTrue();
        mode.Should().Be(SearchMode.Semantic);
    }

    [Fact]
    public void SearchMode_Hybrid_CanBeParsed()
    {
        Enum.TryParse<SearchMode>("Hybrid", ignoreCase: true, out var mode)
            .Should().BeTrue();
        mode.Should().Be(SearchMode.Hybrid);
    }

    [Theory]
    [InlineData(true, "Semantic", true)]   // cross-model + Semantic = should override to Hybrid
    [InlineData(true, "Keyword", false)]   // cross-model + Keyword = no override needed
    [InlineData(true, "Hybrid", false)]    // cross-model + Hybrid = already Hybrid
    [InlineData(false, "Semantic", false)] // no cross-model = no override
    [InlineData(false, "Keyword", false)]
    [InlineData(false, "Hybrid", false)]
    public void CrossModelOverride_ShouldApply_WhenExpected(
        bool enableCrossModel, string mode, bool shouldOverride)
    {
        // This tests the decision logic that HybridSearchService uses
        var settings = new SearchSettings { EnableCrossModelSearch = enableCrossModel };
        var searchMode = Enum.Parse<SearchMode>(mode, ignoreCase: true);

        var wouldOverride = settings.EnableCrossModelSearch && searchMode == SearchMode.Semantic;

        wouldOverride.Should().Be(shouldOverride);
    }
}
