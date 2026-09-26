using Connapse.Search.Keyword;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Connapse.Core.Tests.Search;

[Trait("Category", "Unit")]
public class KeywordSearchServiceTests
{
    private readonly KeywordSearchService _service;

    public KeywordSearchServiceTests()
    {
        var options = new DbContextOptionsBuilder<KnowledgeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var context = new KnowledgeDbContext(options);
        var logger = Substitute.For<ILogger<KeywordSearchService>>();
        _service = new KeywordSearchService(context, logger);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task SearchAsync_EmptyOrWhitespaceQuery_ReturnsEmpty(string? query)
    {
        var options = new Connapse.Core.SearchOptions { TopK = 10, ContainerId = Guid.NewGuid().ToString() };

        var results = await _service.SearchAsync(query!, options, SearchScopes.Unrestricted);

        results.Should().BeEmpty();
    }

    [Theory]
    [InlineData("does calcium intake prevent fractures", false)]
    [InlineData("state-of-the-art 25-hydroxyvitamin", false)]
    [InlineData("organic farming", false)]
    [InlineData("\"bone density\"", true)]
    [InlineData("calcium -supplements", true)]
    [InlineData("calcium OR magnesium", true)]
    [InlineData("calcium or magnesium", true)]
    public void UsesSearchSyntax_Query_DetectsPhrasesExclusionsAndOr(string query, bool expected)
    {
        KeywordSearchService.UsesSearchSyntax(query).Should().Be(expected);
    }
}
