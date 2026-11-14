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
    private readonly IFileTypeValidator _fileTypeValidator;
    private readonly IServiceProvider _services;

    public McpToolsBulkUploadTests()
    {
        _containerStore = ContainerStoreFakes.Create();
        _uploadService = Substitute.For<IUploadService>();
        _fileTypeValidator = Substitute.For<IFileTypeValidator>();

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

        // Accept all file types by default so existing tests are unaffected
        _fileTypeValidator.IsSupported(Arg.Any<string>()).Returns(true);
        _fileTypeValidator.SupportedExtensions.Returns(new HashSet<string>());

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IContainerStore)).Returns(_containerStore);
        services.GetService(typeof(IUploadService)).Returns(_uploadService);
        services.GetService(typeof(IFileTypeValidator)).Returns(_fileTypeValidator);
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
        var result = await McpTools.BulkUpload(McpCaller.Uploads(_services).BulkUpload, McpCaller.Uploads(_services).Container, McpCaller.Http, ContainerId.ToString(), json);

        result.Should().Contain("Uploaded 2 of 2");
        await _uploadService.Received(1).BulkUploadAsync(
            Arg.Is<BulkUploadRequest>(r => r.Files.Count == 2),
            Arg.Any<CancellationToken>());
    }

    private Task<string> UploadFile(string files, string fileName = "", string? textContent = null, string? path = null) =>
        UploadFileJson(System.Text.Json.JsonSerializer.SerializeToElement(files), fileName, textContent, path);

    /// <summary>Calls upload_file with 'files' as the raw JSON an MCP client sent, which need not be a string.</summary>
    private Task<string> UploadFileJson(System.Text.Json.JsonElement? files, string fileName = "", string? textContent = null, string? path = null) =>
        McpTools.UploadFile(McpCaller.Uploads(_services).Upload, McpCaller.Uploads(_services).BulkUpload, McpCaller.Uploads(_services).Container,
            McpCaller.Http, ContainerId.ToString(), textContent: textContent, fileName: fileName, path: path, files: files);

    private static System.Text.Json.JsonElement Json(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task UploadFile_FilesSentAsAList_UploadsEach()
    {
        var result = await UploadFileJson(Json("""[{"filename":"a.txt","content":"hello"},{"filename":"b.txt","content":"world"}]"""));

        result.Should().StartWith("Uploaded 2 of 2 file(s)");
        await _uploadService.Received(1).BulkUploadAsync(Arg.Is<BulkUploadRequest>(r => r.Files.Count == 2), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("""{"filename":"a.txt","content":"hello"}""")]
    [InlineData("42")]
    [InlineData("true")]
    public async Task UploadFile_FilesNeitherAListNorText_SaysWhatShapeItExpects(string json)
    {
        var result = await UploadFileJson(Json(json));

        result.Should().Be(McpTools.FilesShapeError);
        result.Should().Contain("JSON array of file objects").And.Contain("\"filename\"");
        await _uploadService.DidNotReceiveWithAnyArgs().BulkUploadAsync(default!, default);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"filename":"a.txt"}""")]
    public async Task UploadFile_FilesTextNotAList_SaysWhatShapeItExpects(string files)
    {
        (await UploadFile(files)).Should().Be(McpTools.FilesShapeError);
        await _uploadService.DidNotReceiveWithAnyArgs().BulkUploadAsync(default!, default);
    }

    [Fact]
    public async Task UploadFile_FilesListWithASingleFilesArgument_ReturnsErrorWithoutUploading()
    {
        var result = await UploadFileJson(Json("""[{"filename":"a.txt","content":"hello"}]"""), fileName: "a.txt");

        result.Should().StartWith("Error: Provide either 'files' or a single file's");
        await _uploadService.DidNotReceiveWithAnyArgs().BulkUploadAsync(default!, default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public async Task UploadFile_FilesAbsentOrNull_FallsBackToTheSingleFileForm(string? json)
    {
        var result = await UploadFileJson(json is null ? null : Json(json));

        result.Should().Be("Error: 'fileName' is required (or 'files' for several files).");
    }

    [Fact]
    public async Task UploadFile_FilesUndefined_FallsBackToTheSingleFileForm() =>
        (await UploadFileJson(default(System.Text.Json.JsonElement))).Should().Be("Error: 'fileName' is required (or 'files' for several files).");

    [Fact]
    public async Task UploadFile_WithFiles_UploadsEachAndGivesTheBulkUploadSummary()
    {
        var json = """[{"filename":"a.txt","content":"hello"},{"filename":"b.txt","content":"world"}]""";

        var result = await UploadFile(json);

        result.Should().Be("Uploaded 2 of 2 file(s) to container 'test'.\n\nAll files queued for ingestion (parsing, chunking, embedding).");
        await _uploadService.Received(1).BulkUploadAsync(Arg.Is<BulkUploadRequest>(r => r.Files.Count == 2), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("a.txt", null, null)]
    [InlineData("", "hello", null)]
    [InlineData("", null, "/docs/")]
    public async Task UploadFile_FilesWithASingleFilesArgument_ReturnsErrorWithoutUploading(string fileName, string? textContent, string? path)
    {
        var result = await UploadFile("""[{"filename":"a.txt","content":"hello"}]""", fileName, textContent, path);

        result.Should().StartWith("Error: Provide either 'files' or a single file's");
        await _uploadService.DidNotReceiveWithAnyArgs().BulkUploadAsync(default!, default);
    }

    [Fact]
    public async Task UploadFile_NoFileNameAndNoFiles_ReturnsError() =>
        (await UploadFile("")).Should().Be("Error: 'fileName' is required (or 'files' for several files).");

    [Fact]
    public async Task BulkUpload_Base64File_DecodesAndUploads()
    {
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("test content"));
        var json = $$"""[{"filename":"test.bin","content":"{{b64}}","encoding":"base64"}]""";
        var result = await McpTools.BulkUpload(McpCaller.Uploads(_services).BulkUpload, McpCaller.Uploads(_services).Container, McpCaller.Http, ContainerId.ToString(), json);

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
        var result = await McpTools.BulkUpload(McpCaller.Uploads(_services).BulkUpload, McpCaller.Uploads(_services).Container, McpCaller.Http, ContainerId.ToString(), json);

        result.Should().Contain("Uploaded 1 of 2");
        result.Should().Contain("bad.bin");
    }

    [Fact]
    public async Task BulkUpload_SharedBatchId()
    {
        var json = """[{"filename":"a.txt","content":"x"},{"filename":"b.txt","content":"y"}]""";
        var result = await McpTools.BulkUpload(McpCaller.Uploads(_services).BulkUpload, McpCaller.Uploads(_services).Container, McpCaller.Http, ContainerId.ToString(), json);

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
        var result = await McpTools.BulkUpload(McpCaller.Uploads(_services).BulkUpload, McpCaller.Uploads(_services).Container, McpCaller.Http, ContainerId.ToString(), json);

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
        var result = await McpTools.BulkUpload(McpCaller.Uploads(_services).BulkUpload, McpCaller.Uploads(_services).Container, McpCaller.Http, "nonexistent", json);

        result.Should().StartWith("Error:");
        result.Should().Contain("not found");
    }

    [Fact]
    public async Task BulkUpload_ExceedsLimit_ReturnsError()
    {
        var items = Enumerable.Range(0, 101)
            .Select(i => $"{{\"filename\":\"f{i}.txt\",\"content\":\"x\"}}");
        var json = $"[{string.Join(",", items)}]";
        var result = await McpTools.BulkUpload(McpCaller.Uploads(_services).BulkUpload, McpCaller.Uploads(_services).Container, McpCaller.Http, ContainerId.ToString(), json);

        result.Should().StartWith("Error:");
        result.Should().Contain("100");
    }

    [Fact]
    public async Task BulkUpload_EmptyArray_ReturnsError()
    {
        var result = await McpTools.BulkUpload(McpCaller.Uploads(_services).BulkUpload, McpCaller.Uploads(_services).Container, McpCaller.Http, ContainerId.ToString(), "[]");

        result.Should().StartWith("Error:");
    }

    [Fact]
    public async Task BulkUpload_MissingFilename_ReportsPerItemError()
    {
        var json = """[{"content":"hello"}]""";
        var result = await McpTools.BulkUpload(McpCaller.Uploads(_services).BulkUpload, McpCaller.Uploads(_services).Container, McpCaller.Http, ContainerId.ToString(), json);

        result.Should().Contain("Uploaded 0 of 1");
        result.Should().Contain("filename");
    }

    [Fact]
    public async Task BulkUpload_MissingContent_ReportsPerItemError()
    {
        var json = """[{"filename":"no-content.txt"}]""";
        var result = await McpTools.BulkUpload(McpCaller.Uploads(_services).BulkUpload, McpCaller.Uploads(_services).Container, McpCaller.Http, ContainerId.ToString(), json);

        result.Should().Contain("Uploaded 0 of 1");
        result.Should().Contain("content");
    }

    [Fact]
    public async Task BulkUpload_InvalidJson_ReturnsError()
    {
        var result = await McpTools.BulkUpload(McpCaller.Uploads(_services).BulkUpload, McpCaller.Uploads(_services).Container, McpCaller.Http, ContainerId.ToString(), "not json");

        result.Should().Be(McpTools.FilesShapeError);
    }

    private static Container MakeContainer() => new(
        Id: ContainerId.ToString(),
        Name: "test",
        Description: null,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow);
}
