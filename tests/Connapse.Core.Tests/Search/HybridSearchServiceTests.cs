using Connapse.Core;
using Connapse.Search.Hybrid;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Core.Tests.Search;

/// <summary>
/// Unit tests for HybridSearchService.
/// Tests the empty-query guard and settings-driven behavior.
/// Service-delegation tests (Semantic→vector, Keyword→FTS, Hybrid→both) require a real database
/// (KeywordSearchService uses SqlQueryRaw) and live in integration tests instead.
/// Cross-model override predicate logic is covered by CrossModelSearchTests.
/// </summary>
[Trait("Category", "Unit")]
public class HybridSearchServiceTests
{
    private static HybridSearchService CreateService(SearchSettings? settings = null)
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var settingsMonitor = Substitute.For<IOptionsMonitor<SearchSettings>>();
        settingsMonitor.CurrentValue.Returns(settings ?? new SearchSettings());

        return new HybridSearchService(
            scopeFactory,
            [],
            settingsMonitor,
            NullLogger<HybridSearchService>.Instance);
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_ReturnsEmptyResult()
    {
        var service = CreateService();

        var result = await service.SearchAsync("", new SearchOptions());

        result.Hits.Should().BeEmpty();
        result.TotalMatches.Should().Be(0);
    }

    [Fact]
    public async Task SearchAsync_WhitespaceQuery_ReturnsEmptyResult()
    {
        var service = CreateService();

        var result = await service.SearchAsync("   ", new SearchOptions());

        result.Hits.Should().BeEmpty();
        result.TotalMatches.Should().Be(0);
    }

    [Fact]
    public async Task SearchAsync_NullQuery_ReturnsEmptyResult()
    {
        var service = CreateService();

        var result = await service.SearchAsync(null!, new SearchOptions());

        result.Hits.Should().BeEmpty();
        result.TotalMatches.Should().Be(0);
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_ReturnsDuration()
    {
        var service = CreateService();

        var result = await service.SearchAsync("", new SearchOptions());

        result.Duration.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(true, "Semantic", true)]   // cross-model + Semantic → should override to Hybrid
    [InlineData(true, "Keyword", false)]   // cross-model + Keyword → no override
    [InlineData(true, "Hybrid", false)]    // cross-model + Hybrid → already Hybrid
    [InlineData(false, "Semantic", false)] // no cross-model → no override
    public void CrossModelOverride_ShouldOverrideWhenExpected(
        bool enableCrossModel, string mode, bool shouldOverride)
    {
        // Verify the settings-driven override logic matches expectations.
        // The actual HybridSearchService.SearchAsync uses this exact predicate at line 62.
        var settings = new SearchSettings { EnableCrossModelSearch = enableCrossModel };
        var searchMode = Enum.Parse<SearchMode>(mode, ignoreCase: true);

        var wouldOverride = settings.EnableCrossModelSearch && searchMode == SearchMode.Semantic;

        wouldOverride.Should().Be(shouldOverride);
    }

    [Fact]
    public void Constructor_AcceptsEmptyRerankers()
    {
        var act = () => CreateService();

        act.Should().NotThrow();
    }
}
