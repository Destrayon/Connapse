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
    private readonly IUploadService _uploadService;
    private readonly IServiceProvider _services;

    public McpToolsBulkUploadTests()
    {
        _containerStore = Substitute.For<IContainerStore>();
        _uploadService = Substitute.For<IUploadService>();

        var container = MakeContainer();
        _containerStore
            .GetAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(container);

        // Default: uploads succeed
        _uploadService.BulkUploadAsync(Arg.Any<BulkUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var req = ci.ArgAt<BulkUploadRequest>(0);
                var results = req.Files.Select(f =>
                    new UploadResult(true, Guid.NewGuid().ToString(), Guid.NewGuid().ToString())).ToList();
                return new BulkUploadResult(results.Count, 0, Guid.NewGuid().ToString(), results);
            });

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IContainerStore)).Returns(_containerStore);
        services.GetService(typeof(IUploadService)).Returns(_uploadService);
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
        await _uploadService.Received(1).BulkUploadAsync(
            Arg.Is<BulkUploadRequest>(r => r.Files.Count == 2),
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
        // Only 1 valid file gets to IUploadService, the invalid base64 is caught at transport level
        _uploadService.BulkUploadAsync(Arg.Any<BulkUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var req = ci.ArgAt<BulkUploadRequest>(0);
                var results = req.Files.Select(f =>
                    new UploadResult(true, Guid.NewGuid().ToString(), Guid.NewGuid().ToString())).ToList();
                return new BulkUploadResult(results.Count, 0, Guid.NewGuid().ToString(), results);
            });

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
        var result = await McpTools.BulkUpload(_services, ContainerId.ToString(), json);

        // BulkUploadAsync is called once with both files
        await _uploadService.Received(1).BulkUploadAsync(
            Arg.Is<BulkUploadRequest>(r => r.Files.Count == 2),
            Arg.Any<CancellationToken>());
        result.Should().Contain("Uploaded 2 of 2");
    }

    [Fact]
    public async Task BulkUpload_WithFolderPath_CreatesCorrectPath()
    {
        var json = """[{"filename":"doc.md","content":"# Hello","folderPath":"/notes/"}]""";
        var result = await McpTools.BulkUpload(_services, ContainerId.ToString(), json);

        result.Should().Contain("Uploaded 1 of 1");
        await _uploadService.Received(1).BulkUploadAsync(
            Arg.Is<BulkUploadRequest>(r =>
                r.Files[0].FileName == "doc.md" &&
                r.Files[0].Path == "/notes/"),
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
