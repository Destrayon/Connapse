using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Mcp;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Mcp;

[Trait("Category", "Unit")]
public class McpToolsContainerDescribeTests
{
    private static readonly Guid ContainerId = Guid.NewGuid();

    private readonly IContainerStore _containerStore;
    private readonly IDocumentStore _documentStore;
    private readonly IServiceProvider _services;

    public McpToolsContainerDescribeTests()
    {
        _containerStore = Substitute.For<IContainerStore>();
        _documentStore = Substitute.For<IDocumentStore>();

        _containerStore
            .GetAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(MakeContainer());

        _documentStore
            .GetContainerStatsAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(new ContainerStats(12, 12, 0, 0, 300, 2_097_152, new DateTime(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc)));

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IContainerStore)).Returns(_containerStore);
        services.GetService(typeof(IDocumentStore)).Returns(_documentStore);
        _services = services;
    }

    [Fact]
    public async Task ContainerDescribe_WithSummary_ReturnsFullDescription()
    {
        var result = await McpTools.ContainerDescribe(_services, ContainerId.ToString());

        result.Should().Contain("Container: research-papers");
        result.Should().Contain($"ID: {ContainerId}");
        result.Should().Contain("Type: ManagedStorage");
        result.Should().Contain("Description: Academic research papers");
        result.Should().Contain("Summary: A collection of AI and machine learning research.");
        result.Should().Contain("Summary generated:");
        result.Should().Contain("Documents: 12");
        result.Should().Contain("Storage: 2.0 MB");
        result.Should().Contain("Created:");
    }

    [Fact]
    public async Task ContainerDescribe_WithoutSummary_ShowsNotGenerated()
    {
        _containerStore
            .GetAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(MakeContainer(summary: null));

        var result = await McpTools.ContainerDescribe(_services, ContainerId.ToString());

        result.Should().Contain("Summary: (not yet generated)");
    }

    [Fact]
    public async Task ContainerDescribe_ProcessingDocuments_ShowsStatusBreakdown()
    {
        _documentStore
            .GetContainerStatsAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(new ContainerStats(10, 7, 2, 1, 100, 512, null));

        var result = await McpTools.ContainerDescribe(_services, ContainerId.ToString());

        result.Should().Contain("7 ready");
        result.Should().Contain("2 processing");
        result.Should().Contain("1 failed");
    }

    [Fact]
    public async Task ContainerDescribe_AllReady_OmitsStatusBreakdown()
    {
        var result = await McpTools.ContainerDescribe(_services, ContainerId.ToString());

        result.Should().Contain("Documents: 12");
        result.Should().NotContain("ready");
    }

    [Fact]
    public async Task ContainerDescribe_ContainerNotFound_ReturnsError()
    {
        var result = await McpTools.ContainerDescribe(_services, "nonexistent");

        result.Should().StartWith("Error:");
        result.Should().Contain("not found");
    }

    [Fact]
    public async Task ContainerDescribe_ResolvesByName()
    {
        _containerStore
            .GetByNameAsync("research-papers", Arg.Any<CancellationToken>())
            .Returns(MakeContainer());

        var result = await McpTools.ContainerDescribe(_services, "research-papers");

        result.Should().Contain("Container: research-papers");
    }

    [Fact]
    public async Task ContainerDescribe_NoDescription_OmitsDescriptionLine()
    {
        _containerStore
            .GetAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(MakeContainer(description: null));

        var result = await McpTools.ContainerDescribe(_services, ContainerId.ToString());

        result.Should().NotContain("Description:");
    }

    private static Container MakeContainer(
        string? description = "Academic research papers",
        string? summary = "A collection of AI and machine learning research.") =>
        new(
            Id: ContainerId.ToString(),
            Name: "research-papers",
            Description: description,
            ConnectorType: ConnectorType.ManagedStorage,
            CreatedAt: new DateTime(2026, 1, 10, 8, 0, 0, DateTimeKind.Utc),
            UpdatedAt: new DateTime(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc),
            Summary: summary,
            SummaryGeneratedAt: summary is not null ? new DateTime(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc) : null);
}
