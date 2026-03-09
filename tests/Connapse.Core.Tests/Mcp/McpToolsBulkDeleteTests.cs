using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Connapse.Core.Tests.Mcp;

[Trait("Category", "Unit")]
public class McpToolsBulkDeleteTests
{
    private static readonly Guid ContainerId = Guid.NewGuid();

    private readonly IContainerStore _containerStore;
    private readonly IDocumentStore _documentStore;
    private readonly IKnowledgeFileSystem _fileSystem;
    private readonly ILogger<McpTools> _logger;
    private readonly IServiceProvider _services;

    public McpToolsBulkDeleteTests()
    {
        _containerStore = Substitute.For<IContainerStore>();
        _documentStore = Substitute.For<IDocumentStore>();
        _fileSystem = Substitute.For<IKnowledgeFileSystem>();
        _logger = Substitute.For<ILogger<McpTools>>();

        _containerStore
            .GetAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(MakeContainer());

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IContainerStore)).Returns(_containerStore);
        services.GetService(typeof(IDocumentStore)).Returns(_documentStore);
        services.GetService(typeof(IKnowledgeFileSystem)).Returns(_fileSystem);
        services.GetService(typeof(ILogger<McpTools>)).Returns(_logger);
        _services = services;
    }

    [Fact]
    public async Task BulkDelete_AllSucceed_ReturnsSummary()
    {
        var doc1 = MakeDocument("file-1", "a.txt", "/a.txt");
        var doc2 = MakeDocument("file-2", "b.txt", "/b.txt");
        var doc3 = MakeDocument("file-3", "c.txt", "/c.txt");

        _documentStore.GetAsync("file-1", Arg.Any<CancellationToken>()).Returns(doc1);
        _documentStore.GetAsync("file-2", Arg.Any<CancellationToken>()).Returns(doc2);
        _documentStore.GetAsync("file-3", Arg.Any<CancellationToken>()).Returns(doc3);

        var json = """["file-1","file-2","file-3"]""";
        var result = await McpTools.BulkDelete(_services, ContainerId.ToString(), json);

        result.Should().Contain("Deleted 3 of 3");
        await _documentStore.Received(3).DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BulkDelete_PartialFailure_ReportsEachResult()
    {
        var doc1 = MakeDocument("file-1", "a.txt", "/a.txt");
        _documentStore.GetAsync("file-1", Arg.Any<CancellationToken>()).Returns(doc1);
        _documentStore.GetAsync("file-2", Arg.Any<CancellationToken>()).Returns((Document?)null);

        var json = """["file-1","file-2"]""";
        var result = await McpTools.BulkDelete(_services, ContainerId.ToString(), json);

        result.Should().Contain("Deleted 1 of 2");
        result.Should().Contain("file-2");
    }

    [Fact]
    public async Task BulkDelete_StorageCleanupFails_StillReportsSuccess()
    {
        var doc1 = MakeDocument("file-1", "a.txt", "/a.txt");
        _documentStore.GetAsync("file-1", Arg.Any<CancellationToken>()).Returns(doc1);
        _fileSystem
            .DeleteAsync("/a.txt", Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("disk error"));

        var json = """["file-1"]""";
        var result = await McpTools.BulkDelete(_services, ContainerId.ToString(), json);

        result.Should().Contain("Deleted 1 of 1");
        result.Should().Contain("Warnings");
        result.Should().Contain("storage cleanup failed");
    }

    [Fact]
    public async Task BulkDelete_ContainerNotFound_ReturnsError()
    {
        var json = """["file-1"]""";
        var result = await McpTools.BulkDelete(_services, "nonexistent", json);

        result.Should().StartWith("Error:");
        result.Should().Contain("not found");
    }

    [Fact]
    public async Task BulkDelete_ExceedsLimit_ReturnsError()
    {
        var ids = Enumerable.Range(0, 101).Select(i => $"\"file-{i}\"");
        var json = $"[{string.Join(",", ids)}]";
        var result = await McpTools.BulkDelete(_services, ContainerId.ToString(), json);

        result.Should().StartWith("Error:");
        result.Should().Contain("100");
    }

    [Fact]
    public async Task BulkDelete_EmptyArray_ReturnsError()
    {
        var result = await McpTools.BulkDelete(_services, ContainerId.ToString(), "[]");

        result.Should().StartWith("Error:");
    }

    [Fact]
    public async Task BulkDelete_InvalidJson_ReturnsError()
    {
        var result = await McpTools.BulkDelete(_services, ContainerId.ToString(), "not json");

        result.Should().StartWith("Error:");
    }

    private static Container MakeContainer() => new(
        Id: ContainerId.ToString(),
        Name: "test",
        Description: null,
        ConnectorType: ConnectorType.MinIO,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow);

    private static Document MakeDocument(string id, string fileName, string path) => new(
        Id: id,
        ContainerId: ContainerId.ToString(),
        FileName: fileName,
        ContentType: "text/plain",
        Path: path,
        SizeBytes: 100,
        CreatedAt: DateTime.UtcNow,
        Metadata: new());
}
