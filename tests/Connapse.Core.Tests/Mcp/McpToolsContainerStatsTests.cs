using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Vectors;
using Connapse.Web.Mcp;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Connapse.Core.Tests.Mcp;

[Trait("Category", "Unit")]
public class McpToolsContainerStatsTests
{
    private static readonly Guid ContainerId = Guid.NewGuid();

    private readonly IContainerStore _containerStore;
    private readonly IDocumentStore _documentStore;
    private readonly VectorModelDiscovery _modelDiscovery;
    private readonly IServiceProvider _services;

    public McpToolsContainerStatsTests()
    {
        _containerStore = Substitute.For<IContainerStore>();
        _documentStore = Substitute.For<IDocumentStore>();
        _modelDiscovery = Substitute.ForPartsOf<VectorModelDiscovery>(
            Substitute.For<IDbContextFactory<KnowledgeDbContext>>(),
            Substitute.For<ILogger<VectorModelDiscovery>>());

        _containerStore
            .GetAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(MakeContainer());

        _documentStore
            .GetContainerStatsAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(new ContainerStats(10, 8, 1, 1, 250, 1_048_576, new DateTime(2026, 3, 5, 14, 0, 0, DateTimeKind.Utc)));

        _modelDiscovery
            .GetModelsAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(new List<EmbeddingModelInfo>
            {
                new("text-embedding-3-small", 1536, 240)
            });

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IContainerStore)).Returns(_containerStore);
        services.GetService(typeof(IDocumentStore)).Returns(_documentStore);
        services.GetService(typeof(VectorModelDiscovery)).Returns(_modelDiscovery);
        _services = services;
    }

    [Fact]
    public async Task ContainerStats_ReturnsFormattedStats()
    {
        var result = await McpTools.ContainerStats(_services, ContainerId.ToString());

        result.Should().Contain("Container: test-container");
        result.Should().Contain("Type: MinIO");
        result.Should().Contain("Documents: 10 (8 ready, 1 processing, 1 failed)");
        result.Should().Contain("Chunks: 250");
        result.Should().Contain("Storage: 1.0 MB");
        result.Should().Contain("Embedding model: text-embedding-3-small (1536 dims, 240 vectors)");
        result.Should().Contain("Last indexed: 2026-03-05");
    }

    [Fact]
    public async Task ContainerStats_AllReady_OmitsStatusBreakdown()
    {
        _documentStore
            .GetContainerStatsAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(new ContainerStats(5, 5, 0, 0, 100, 512, null));

        _modelDiscovery
            .GetModelsAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(new List<EmbeddingModelInfo>());

        var result = await McpTools.ContainerStats(_services, ContainerId.ToString());

        result.Should().Contain("Documents: 5");
        result.Should().NotContain("ready");
        result.Should().Contain("Embedding model: none");
        result.Should().Contain("Last indexed: never");
    }

    [Fact]
    public async Task ContainerStats_ContainerNotFound_ReturnsError()
    {
        var result = await McpTools.ContainerStats(_services, "nonexistent");

        result.Should().StartWith("Error:");
        result.Should().Contain("not found");
    }

    [Fact]
    public async Task ContainerStats_ResolvesByName()
    {
        _containerStore
            .GetByNameAsync("test-container", Arg.Any<CancellationToken>())
            .Returns(MakeContainer());

        var result = await McpTools.ContainerStats(_services, "test-container");

        result.Should().Contain("Container: test-container");
    }

    [Fact]
    public async Task ContainerStats_EmptyContainer_ReturnsZeros()
    {
        _documentStore
            .GetContainerStatsAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(new ContainerStats(0, 0, 0, 0, 0, 0, null));

        _modelDiscovery
            .GetModelsAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(new List<EmbeddingModelInfo>());

        var result = await McpTools.ContainerStats(_services, ContainerId.ToString());

        result.Should().Contain("Documents: 0");
        result.Should().Contain("Chunks: 0");
        result.Should().Contain("Storage: 0 B");
        result.Should().Contain("Embedding model: none");
        result.Should().Contain("Last indexed: never");
    }

    private static Container MakeContainer() => new(
        Id: ContainerId.ToString(),
        Name: "test-container",
        Description: "A test container",
        ConnectorType: ConnectorType.MinIO,
        CreatedAt: new DateTime(2026, 2, 28, 10, 0, 0, DateTimeKind.Utc),
        UpdatedAt: new DateTime(2026, 3, 5, 14, 0, 0, DateTimeKind.Utc));
}
