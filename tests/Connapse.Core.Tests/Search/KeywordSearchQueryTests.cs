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
    [InlineData("does calcium intake or diet matter", new[] { "does", "calcium", "intake", "or", "diet", "matter" }, new string[0])]
    [InlineData("state-of-the-art 25-hydroxyvitamin", new[] { "state-of-the-art", "25-hydroxyvitamin" }, new string[0])]
    [InlineData("who directed \"The Grand Budapest Hotel\"?", new[] { "who", "directed", "The Grand Budapest Hotel", "?" }, new string[0])]
    [InlineData("calcium -supplements -\"vitamin d\"", new[] { "calcium" }, new[] { "supplements", "vitamin d" })]
    [InlineData("a - b", new[] { "a", "-", "b" }, new string[0])]
    [InlineData("say \"unclosed quote", new[] { "say", "unclosed", "quote" }, new string[0])]
    [InlineData("-only -exclusions", new string[0], new[] { "only", "exclusions" })]
    public void Parse_Query_SplitsOptionalClausesFromExclusions(string query, string[] clauses, string[] exclusions)
    {
        var parsed = KeywordQuery.Parse(query);

        parsed.Clauses.Should().Equal(clauses);
        parsed.Exclusions.Should().Equal(exclusions);
    }

    [Fact]
    public async Task SearchAsync_OnlyExclusions_ReturnsEmpty()
    {
        var options = new Connapse.Core.SearchOptions { TopK = 10, ContainerId = Guid.NewGuid().ToString() };

        var results = await _service.SearchAsync("-calcium", options, SearchScopes.Unrestricted);

        results.Should().BeEmpty();
    }
}
