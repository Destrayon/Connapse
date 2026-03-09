using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Mcp;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Mcp;

[Trait("Category", "Unit")]
public class McpToolsBulkUploadTests
{
    private static readonly Guid ContainerId = Guid.NewGuid();

    private readonly IContainerStore _containerStore;
    private readonly IDocumentStore _documentStore;
    private readonly IIngestionQueue _ingestionQueue;
    private readonly IConnectorFactory _connectorFactory;
    private readonly IConnector _connector;
    private readonly IFolderStore _folderStore;
    private readonly IServiceProvider _services;

    public McpToolsBulkUploadTests()
    {
        _containerStore = Substitute.For<IContainerStore>();
        _documentStore = Substitute.For<IDocumentStore>();
        _ingestionQueue = Substitute.For<IIngestionQueue>();
        _connectorFactory = Substitute.For<IConnectorFactory>();
        _connector = Substitute.For<IConnector>();
        _folderStore = Substitute.For<IFolderStore>();

        var container = MakeContainer();
        _containerStore
            .GetAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(container);

        _connectorFactory.Create(Arg.Any<Container>()).Returns(_connector);

        _folderStore
            .ExistsAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IContainerStore)).Returns(_containerStore);
        services.GetService(typeof(IDocumentStore)).Returns(_documentStore);
        services.GetService(typeof(IIngestionQueue)).Returns(_ingestionQueue);
        services.GetService(typeof(IConnectorFactory)).Returns(_connectorFactory);
        services.GetService(typeof(IFolderStore)).Returns(_folderStore);
        _services = services;
    }

    [Fact]
    public async Task BulkUpload_MultipleTextFiles_AllSucceed()
    {
        var json = """
        [
            {"filename":"a.txt","content":"hello"},
            {"filename":"b.txt","content":"world"}
        ]
        """;
        var result = await McpTools.BulkUpload(_services, ContainerId.ToString(), json);

        result.Should().Contain("Uploaded 2 of 2");
        await _ingestionQueue.Received(2).EnqueueAsync(
            Arg.Is<IngestionJob>(j => j.BatchId != null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BulkUpload_Base64File_DecodesAndUploads()
    {
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("test content"));
        var json = $$"""[{"filename":"test.bin","content":"{{b64}}","encoding":"base64"}]""";
        var result = await McpTools.BulkUpload(_services, ContainerId.ToString(), json);

        result.Should().Contain("Uploaded 1 of 1");
    }

    [Fact]
    public async Task BulkUpload_InvalidBase64_ReportsPerItemError()
    {
        var json = """
        [
            {"filename":"good.txt","content":"hello"},
            {"filename":"bad.bin","content":"!!!not-base64!!!","encoding":"base64"}
        ]
        """;
        var result = await McpTools.BulkUpload(_services, ContainerId.ToString(), json);

        result.Should().Contain("Uploaded 1 of 2");
        result.Should().Contain("bad.bin");
    }

    [Fact]
    public async Task BulkUpload_SharedBatchId()
    {
        var json = """[{"filename":"a.txt","content":"x"},{"filename":"b.txt","content":"y"}]""";
        await McpTools.BulkUpload(_services, ContainerId.ToString(), json);

        var jobs = _ingestionQueue.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "EnqueueAsync")
            .Select(c => (IngestionJob)c.GetArguments()[0]!)
            .ToList();

        jobs.Should().HaveCount(2);
        jobs[0].BatchId.Should().NotBeNullOrEmpty();
        jobs[0].BatchId.Should().Be(jobs[1].BatchId);
    }

    [Fact]
    public async Task BulkUpload_WithFolderPath_CreatesCorrectPath()
    {
        var json = """[{"filename":"doc.md","content":"# Hello","folderPath":"/notes/"}]""";
        var result = await McpTools.BulkUpload(_services, ContainerId.ToString(), json);

        result.Should().Contain("Uploaded 1 of 1");
        await _connector.Received(1).WriteFileAsync(
            Arg.Is<string>(p => p.Contains("notes") && p.Contains("doc.md")),
            Arg.Any<Stream>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BulkUpload_ContainerNotFound_ReturnsError()
    {
        var json = """[{"filename":"a.txt","content":"x"}]""";
        var result = await McpTools.BulkUpload(_services, "nonexistent", json);

        result.Should().StartWith("Error:");
        result.Should().Contain("not found");
    }

    [Fact]
    public async Task BulkUpload_ExceedsLimit_ReturnsError()
    {
        var items = Enumerable.Range(0, 101)
            .Select(i => $"{{\"filename\":\"f{i}.txt\",\"content\":\"x\"}}");
        var json = $"[{string.Join(",", items)}]";
        var result = await McpTools.BulkUpload(_services, ContainerId.ToString(), json);

        result.Should().StartWith("Error:");
        result.Should().Contain("100");
    }

    [Fact]
    public async Task BulkUpload_EmptyArray_ReturnsError()
    {
        var result = await McpTools.BulkUpload(_services, ContainerId.ToString(), "[]");

        result.Should().StartWith("Error:");
    }

    [Fact]
    public async Task BulkUpload_MissingFilename_ReportsPerItemError()
    {
        var json = """[{"content":"hello"}]""";
        var result = await McpTools.BulkUpload(_services, ContainerId.ToString(), json);

        result.Should().Contain("Uploaded 0 of 1");
        result.Should().Contain("filename");
    }

    [Fact]
    public async Task BulkUpload_MissingContent_ReportsPerItemError()
    {
        var json = """[{"filename":"no-content.txt"}]""";
        var result = await McpTools.BulkUpload(_services, ContainerId.ToString(), json);

        result.Should().Contain("Uploaded 0 of 1");
        result.Should().Contain("content");
    }

    [Fact]
    public async Task BulkUpload_InvalidJson_ReturnsError()
    {
        var result = await McpTools.BulkUpload(_services, ContainerId.ToString(), "not json");

        result.Should().StartWith("Error:");
    }

    private static Container MakeContainer() => new(
        Id: ContainerId.ToString(),
        Name: "test",
        Description: null,
        ConnectorType: ConnectorType.MinIO,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow);
}
