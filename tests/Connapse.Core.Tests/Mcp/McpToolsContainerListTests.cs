using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Application.Containers;
using Connapse.Application.Sources;
using Connapse.Web.Mcp;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Mcp;

[Trait("Category", "Unit")]
public class McpToolsContainerListTests
{
    private readonly IContainerStore _containerStore;
    private readonly ISourceStore _sourceStore;
    private readonly ListSources _sources;

    public McpToolsContainerListTests()
    {
        _containerStore = Substitute.For<IContainerStore>();

        // container_list now lists sources alongside containers. Configured to return none
        // so these tests keep covering container rendering; source rendering is covered in
        // McpSourceKindTests against a real database.
        _sourceStore = Substitute.For<ISourceStore>();
        _sourceStore.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Source>());
        _sources = new ListSources(_sourceStore, McpCaller.SeesEverySource, McpCaller.Allowed);
    }

    [Fact]
    public async Task ContainerList_EmptyReturnsPlainMessageWithoutTip()
    {
        _containerStore
            .ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Container>());

        var result = await McpTools.ContainerList(new ListContainers(_containerStore, McpCaller.Allowed), _sources, McpCaller.Http);

        result.Should().Be("No containers found.");
    }

    [Fact]
    public async Task ContainerList_SingleContainerReturnsNoTip()
    {
        _containerStore
            .ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Container>
            {
                MakeContainer("only-one", "The only container", 7)
            });

        var result = await McpTools.ContainerList(new ListContainers(_containerStore, McpCaller.Allowed), _sources, McpCaller.Http);

        result.Should().NotStartWith("TIP:");
        result.Should().StartWith("Found 1 knowledge scope(s):");
        result.Should().Contain("- only-one [managed] (7 files) — The only container");
    }

    [Fact]
    public async Task ContainerList_MultipleContainersBeginWithTrimmedTip()
    {
        _containerStore
            .ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Container>
            {
                MakeContainer("docs", "Product documentation", 12),
                MakeContainer("research", "Customer research", 5)
            });

        var result = await McpTools.ContainerList(new ListContainers(_containerStore, McpCaller.Allowed), _sources, McpCaller.Http);

        result.Should().StartWith("TIP:");
        result.Should().Contain("search_knowledge");
        result.Should().Contain("Pick the entry whose description best matches the topic");

        // Trimmed TIP should NOT contain the v1 "Do NOT enumerate files" phrasing —
        // ServerInstructions Rule 3 covers that now.
        result.Should().NotContain("Do NOT enumerate files");

        result.Should().Contain("- docs [managed] (12 files) — Product documentation");
        result.Should().Contain("- research [managed] (5 files) — Customer research");
    }

    private static Container MakeContainer(
        string name,
        string description,
        int docCount) => new(
        Id: Guid.NewGuid().ToString(),
        Name: name,
        Description: description,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow,
        DocumentCount: docCount);
}
