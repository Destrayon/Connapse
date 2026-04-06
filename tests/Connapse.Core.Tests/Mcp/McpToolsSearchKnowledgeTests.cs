using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Mcp;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Core.Tests.Mcp;

[Trait("Category", "Unit")]
public class McpToolsSearchKnowledgeTests
{
    private static readonly Guid ContainerId = Guid.NewGuid();

    private readonly IContainerStore _containerStore;
    private readonly IKnowledgeSearch _searchService;
    private readonly IOptionsMonitor<SearchSettings> _searchSettings;
    private readonly IServiceProvider _services;

    public McpToolsSearchKnowledgeTests()
    {
        _containerStore = Substitute.For<IContainerStore>();
        _searchService = Substitute.For<IKnowledgeSearch>();
        _searchSettings = Substitute.For<IOptionsMonitor<SearchSettings>>();
        _searchSettings.CurrentValue.Returns(new SearchSettings());

        _containerStore
            .GetAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(MakeContainer());

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IContainerStore)).Returns(_containerStore);
        services.GetService(typeof(IKnowledgeSearch)).Returns(_searchService);
        services.GetService(typeof(IOptionsMonitor<SearchSettings>)).Returns(_searchSettings);
        _services = services;
    }

    [Fact]
    public async Task SearchKnowledge_ReturnsScoreAndMetadata()
    {
        var hits = new List<SearchHit>
        {
            new("chunk-1", "doc-1", "Hello world", 0.847f, new Dictionary<string, string>
            {
                { "fileName", "report.pdf" },
                { "path", "/docs/report.pdf" },
                { "chunkIndex", "2" }
            })
        };
        var searchResult = new SearchResult(hits, 1, TimeSpan.FromMilliseconds(42));
        _searchService.SearchAsync("test query", Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(searchResult);

        var result = await McpTools.SearchKnowledge(
            _services, "test query", ContainerId.ToString());

        result.Should().Contain("Score: 0.847");
        result.Should().Contain("File: report.pdf");
        result.Should().Contain("Path: /docs/report.pdf");
        result.Should().Contain("Chunk: 2");
        result.Should().Contain("DocumentId: doc-1");
        result.Should().Contain("Hello world");
    }

    [Fact]
    public async Task SearchKnowledge_NoResults_ReturnsNoResultsMessage()
    {
        var searchResult = new SearchResult([], 0, TimeSpan.FromMilliseconds(10));
        _searchService.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(searchResult);

        var result = await McpTools.SearchKnowledge(
            _services, "no match", ContainerId.ToString());

        result.Should().Be("No results found.");
    }

    [Fact]
    public async Task SearchKnowledge_MultipleResults_NumberedCorrectly()
    {
        var hits = new List<SearchHit>
        {
            new("c1", "d1", "First", 0.9f, new Dictionary<string, string>
            {
                { "fileName", "a.txt" }, { "path", "/a.txt" }, { "chunkIndex", "0" }
            }),
            new("c2", "d2", "Second", 0.7f, new Dictionary<string, string>
            {
                { "fileName", "b.txt" }, { "path", "/b.txt" }, { "chunkIndex", "1" }
            })
        };
        _searchService.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResult(hits, 2, TimeSpan.FromMilliseconds(5)));

        var result = await McpTools.SearchKnowledge(
            _services, "query", ContainerId.ToString());

        result.Should().Contain("--- Result 1 ---");
        result.Should().Contain("--- Result 2 ---");
        result.Should().Contain("Score: 0.900");
        result.Should().Contain("Score: 0.700");
    }

    [Fact]
    public async Task SearchKnowledge_PathFilterWiredToOptions()
    {
        _searchService.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResult([], 0, TimeSpan.FromMilliseconds(1)));

        await McpTools.SearchKnowledge(
            _services, "query", ContainerId.ToString(), path: "/docs/");

        await _searchService.Received(1).SearchAsync(
            "query",
            Arg.Is<SearchOptions>(o =>
                o.Filters != null &&
                o.Filters.ContainsKey("pathPrefix") &&
                o.Filters["pathPrefix"] == "/docs/"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchKnowledge_ContainerNotFound_ReturnsError()
    {
        var unknownId = Guid.NewGuid();

        var result = await McpTools.SearchKnowledge(
            _services, "query", unknownId.ToString());

        result.Should().StartWith("Error:");
        result.Should().Contain("not found");
    }

    [Fact]
    public async Task SearchKnowledge_MissingMetadata_ShowsFallbacks()
    {
        var hits = new List<SearchHit>
        {
            new("chunk-1", "doc-1", "content", 0.5f, new Dictionary<string, string>())
        };
        _searchService.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResult(hits, 1, TimeSpan.FromMilliseconds(1)));

        var result = await McpTools.SearchKnowledge(
            _services, "query", ContainerId.ToString());

        result.Should().Contain("File: unknown");
        result.Should().Contain("Path: /");
        result.Should().Contain("Chunk: 0");
    }

    [Fact]
    public async Task SearchKnowledge_TruncatedResults_ShowsTotalMatchCount()
    {
        var hits = new List<SearchHit>
        {
            new("c1", "d1", "First", 0.9f, new Dictionary<string, string>
            {
                { "fileName", "a.txt" }, { "path", "/a.txt" }, { "chunkIndex", "0" }
            })
        };
        _searchService.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResult(hits, 15, TimeSpan.FromMilliseconds(5)));

        var result = await McpTools.SearchKnowledge(
            _services, "query", ContainerId.ToString());

        result.Should().Contain("Showing 1 of 15 matching chunk(s)");
    }

    [Fact]
    public async Task SearchKnowledge_AllResultsReturned_ShowsFoundCount()
    {
        var hits = new List<SearchHit>
        {
            new("c1", "d1", "First", 0.9f, new Dictionary<string, string>
            {
                { "fileName", "a.txt" }, { "path", "/a.txt" }, { "chunkIndex", "0" }
            })
        };
        _searchService.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResult(hits, 1, TimeSpan.FromMilliseconds(5)));

        var result = await McpTools.SearchKnowledge(
            _services, "query", ContainerId.ToString());

        result.Should().Contain("Found 1 result(s)");
    }

    private static Container MakeContainer() => new(
        Id: ContainerId.ToString(),
        Name: "test",
        Description: null,
        ConnectorType: ConnectorType.ManagedStorage,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow);
}
