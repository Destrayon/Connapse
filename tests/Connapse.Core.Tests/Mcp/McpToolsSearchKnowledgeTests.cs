using System.Security.Claims;
using Connapse.Application.Ports;
using Connapse.Application.Search;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Mcp;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
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
    private readonly SearchKnowledge _search;
    private readonly IHttpContextAccessor _http = Substitute.For<IHttpContextAccessor>();

    public McpToolsSearchKnowledgeTests()
    {
        _containerStore = ContainerStoreFakes.Create();
        _searchService = Substitute.For<IKnowledgeSearch>();
        _searchSettings = Substitute.For<IOptionsMonitor<SearchSettings>>();
        _searchSettings.CurrentValue.Returns(new SearchSettings());

        _containerStore
            .GetAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(MakeContainer());

        // search_knowledge now accepts a source id as well as a container id. Left
        // unconfigured so every source lookup misses and these tests keep covering the
        // container path; the source path is covered in McpSourceKindTests.
        var sourceStore = Substitute.For<ISourceStore>();

        // Who may search is the operation's question, tested in SearchOperationTests; here every caller may.
        var authorizer = Substitute.For<IOperationAuthorizer>();
        authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(true);
        _search = new SearchKnowledge(_containerStore, sourceStore, Substitute.For<ISourceVisibility>(), _searchService, _searchSettings, authorizer);
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
            _search, _http, "test query", ContainerId.ToString());

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
            _search, _http, "no match", ContainerId.ToString());

        result.Should().Be("No results found.");
    }

    [Theory]
    [InlineData(0, 2, "No results found.\nNote: 2 documents in the searched container or source are still waiting to be indexed, so search can't find them yet. Text files can already be read with get_document.")]
    [InlineData(1, 1, "\nNote: 1 document in the searched container or source is still waiting to be indexed, so search can't find it yet. Text files can already be read with get_document.\n\n")]
    [InlineData(1, 0, "(mode: Hybrid):\n\n")]
    public async Task SearchKnowledge_DocumentsWaitingToBeIndexed_SaysSearchCantFindThemYet(int hits, int pending, string says)
    {
        var documents = Substitute.For<IDocumentStore>();
        documents.CountAwaitingIndexAsync(ContainerId, Arg.Any<CancellationToken>()).Returns(pending);
        var authorizer = Substitute.For<IOperationAuthorizer>();
        authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(true);
        var search = new SearchKnowledge(_containerStore, Substitute.For<ISourceStore>(), Substitute.For<ISourceVisibility>(), _searchService,
            _searchSettings, authorizer, documents: documents);
        var found = Enumerable.Range(0, hits).Select(i => new SearchHit($"c{i}", $"d{i}", "text", 0.5f, [])).ToList();
        _searchService.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResult(found, hits, TimeSpan.FromMilliseconds(5)));

        var result = await McpTools.SearchKnowledge(search, _http, "journal", ContainerId.ToString());

        result.Should().Contain(says);
        if (pending == 0)
            result.Should().NotContain("waiting to be indexed");
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
            _search, _http, "query", ContainerId.ToString());

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
            _search, _http, "query", ContainerId.ToString(), path: "/docs/");

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
            _search, _http, "query", unknownId.ToString());

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
            _search, _http, "query", ContainerId.ToString());

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
            _search, _http, "query", ContainerId.ToString());

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
            _search, _http, "query", ContainerId.ToString());

        result.Should().Contain("Found 1 result(s)");
    }

    [Fact]
    public async Task SearchKnowledge_QueryVectorIsTheWrongSize_SaysWhichSettingToChange()
    {
        var mismatch = new Connapse.Storage.Vectors.EmbeddingDimensionMismatchException("Qwen/Qwen3-Embedding-8B", 1536, 4096, sentDimensions: false);
        _searchService.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns<SearchResult>(_ => throw new EmbeddingUnavailableException(mismatch));

        var result = await McpTools.SearchKnowledge(
            _search, _http, "query", ContainerId.ToString());

        result.Should().StartWith("Error:").And.Contain("Set Dimensions to 4096").And.NotContain("Try again shortly");
    }

    [Fact]
    public async Task SearchKnowledge_EmbeddingsAreDown_SaysToRetryOrUseKeyword()
    {
        _searchService.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns<SearchResult>(_ => throw new EmbeddingUnavailableException(new HttpRequestException("ollama:11434")));

        var result = await McpTools.SearchKnowledge(
            _search, _http, "query", ContainerId.ToString());

        result.Should().StartWith("Error:").And.Contain("Try again shortly").And.Contain("Keyword");
    }

    private static Container MakeContainer() => new(
        Id: ContainerId.ToString(),
        Name: "test",
        Description: null,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow);
}
