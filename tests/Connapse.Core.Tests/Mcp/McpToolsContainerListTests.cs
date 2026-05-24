using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Mcp;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Mcp;

[Trait("Category", "Unit")]
public class McpToolsContainerListTests
{
    private readonly IContainerStore _containerStore;
    private readonly IServiceProvider _services;

    public McpToolsContainerListTests()
    {
        _containerStore = Substitute.For<IContainerStore>();

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IContainerStore)).Returns(_containerStore);
        _services = services;
    }

    [Fact]
    public async Task ContainerList_EmptyReturnsPlainMessageWithoutTip()
    {
        _containerStore
            .ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Container>());

        var result = await McpTools.ContainerList(_services);

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

        var result = await McpTools.ContainerList(_services);

        result.Should().NotStartWith("TIP:");
        result.Should().StartWith("Found 1 container(s):");
        result.Should().Contain("- only-one (7 files) — The only container");
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

        var result = await McpTools.ContainerList(_services);

        result.Should().StartWith("TIP:");
        result.Should().Contain("search_knowledge");
        result.Should().Contain("Pick the container whose description best matches the topic");

        // Trimmed TIP should NOT contain the v1 "Do NOT enumerate files" phrasing —
        // ServerInstructions Rule 3 covers that now.
        result.Should().NotContain("Do NOT enumerate files");

        result.Should().Contain("- docs (12 files) — Product documentation");
        result.Should().Contain("- research (5 files) — Customer research");
    }

    private static Container MakeContainer(string name, string description, int docCount) => new(
        Id: Guid.NewGuid().ToString(),
        Name: name,
        Description: description,
        ConnectorType: ConnectorType.ManagedStorage,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow,
        DocumentCount: docCount);
}
