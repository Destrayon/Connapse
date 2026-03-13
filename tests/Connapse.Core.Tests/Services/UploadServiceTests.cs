using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Services;

[Trait("Category", "Unit")]
public class UploadServiceTests
{
    private static readonly Guid ContainerId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly IContainerStore _containerStore = Substitute.For<IContainerStore>();
    private readonly IConnectorFactory _connectorFactory = Substitute.For<IConnectorFactory>();
    private readonly IConnector _connector = Substitute.For<IConnector>();
    private readonly IFolderStore _folderStore = Substitute.For<IFolderStore>();
    private readonly IIngestionQueue _ingestionQueue = Substitute.For<IIngestionQueue>();
    private readonly IFileTypeValidator _fileTypeValidator = Substitute.For<IFileTypeValidator>();
    private readonly ICloudScopeService _cloudScopeService = Substitute.For<ICloudScopeService>();
    private readonly IAuditLogger _auditLogger = Substitute.For<IAuditLogger>();

    private readonly IUploadService _sut;

    public UploadServiceTests()
    {
        var container = MakeContainer();
        _containerStore.GetAsync(ContainerId, Arg.Any<CancellationToken>()).Returns(container);
        _connectorFactory.Create(Arg.Any<Container>()).Returns(_connector);
        _connector.SupportsWrite.Returns(true);
        _connector.ResolveJobPath(Arg.Any<string>()).Returns(ci => "/" + ci.ArgAt<string>(0).TrimStart('/'));
        _fileTypeValidator.IsSupported(Arg.Any<string>()).Returns(true);
        _fileTypeValidator.SupportedExtensions.Returns(new HashSet<string> { ".txt", ".pdf", ".md" });
        _folderStore.ExistsAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        // Placeholder until implementation exists — test compile will fail as expected
        _sut = null!;
    }

    private UploadRequest MakeRequest(
        string fileName = "test.txt",
        byte[]? content = null,
        string? path = null,
        string? strategy = null) =>
        new(ContainerId, fileName, new MemoryStream(content ?? "hello"u8.ToArray()),
            UserId, path, null, strategy, "API");

    private static Container MakeContainer(
        ConnectorType type = ConnectorType.MinIO,
        string? config = null) =>
        new(ContainerId.ToString(), "test", null, type, DateTime.UtcNow, DateTime.UtcNow,
            ConnectorConfig: config);

    // --- Validation tests ---

    [Theory]
    [InlineData("")]
    [InlineData("../evil.txt")]
    [InlineData("path/to/file.txt")]
    public async Task UploadAsync_RejectsInvalidFilename(string fileName)
    {
        var result = await _sut.UploadAsync(MakeRequest(fileName: fileName));
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task UploadAsync_RejectsPathTraversal()
    {
        var result = await _sut.UploadAsync(MakeRequest(path: "/docs/../etc/passwd"));
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("traversal");
    }

    [Fact]
    public async Task UploadAsync_RejectsUnsupportedExtension()
    {
        _fileTypeValidator.IsSupported("bad.xyz").Returns(false);
        var result = await _sut.UploadAsync(MakeRequest(fileName: "bad.xyz"));
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("extension");
    }

    [Fact]
    public async Task UploadAsync_RejectsZeroByte()
    {
        var result = await _sut.UploadAsync(MakeRequest(content: Array.Empty<byte>()));
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("empty");
    }

    [Fact]
    public async Task UploadAsync_RejectsNonexistentContainer()
    {
        _containerStore.GetAsync(ContainerId, Arg.Any<CancellationToken>()).Returns((Container?)null);
        var result = await _sut.UploadAsync(MakeRequest());
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task UploadAsync_RejectsReadOnlyConnector()
    {
        _connector.SupportsWrite.Returns(false);
        var result = await _sut.UploadAsync(MakeRequest());
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("read-only");
    }

    [Fact]
    public async Task UploadAsync_RejectsCloudScopeDenied()
    {
        _containerStore.GetAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(MakeContainer(ConnectorType.S3));
        _connector.SupportsWrite.Returns(true); // Hypothetical writable S3
        _cloudScopeService.GetScopesAsync(UserId, Arg.Any<Container>(), Arg.Any<CancellationToken>())
            .Returns(CloudScopeResult.Deny("test denial"));

        var result = await _sut.UploadAsync(MakeRequest());
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("access");
    }

    // --- Happy path ---

    [Fact]
    public async Task UploadAsync_WritesFileAndEnqueuesJob()
    {
        var result = await _sut.UploadAsync(MakeRequest());

        result.Success.Should().BeTrue();
        result.DocumentId.Should().NotBeNullOrEmpty();
        result.JobId.Should().NotBeNullOrEmpty();

        await _connector.Received(1).WriteFileAsync(
            Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _ingestionQueue.Received(1).EnqueueAsync(
            Arg.Is<IngestionJob>(j =>
                j.Options.Metadata!.ContainsKey("IngestedVia") &&
                j.Options.Metadata["IngestedVia"] == "API"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_CreatesFolders_WhenPathProvided()
    {
        _folderStore.ExistsAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _folderStore.CreateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new Folder(Guid.NewGuid().ToString(), ci.ArgAt<Guid>(0).ToString(),
                ci.ArgAt<string>(1), DateTime.UtcNow));

        var result = await _sut.UploadAsync(MakeRequest(path: "/docs/reports/"));

        result.Success.Should().BeTrue();
        await _folderStore.Received(1).CreateAsync(ContainerId, "/docs/", Arg.Any<CancellationToken>());
        await _folderStore.Received(1).CreateAsync(ContainerId, "/docs/reports/", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_DefaultsToSemanticStrategy()
    {
        var result = await _sut.UploadAsync(MakeRequest());

        result.Success.Should().BeTrue();
        await _ingestionQueue.Received(1).EnqueueAsync(
            Arg.Is<IngestionJob>(j => j.Options.Strategy == ChunkingStrategy.Semantic),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_UsesProvidedStrategy()
    {
        var result = await _sut.UploadAsync(MakeRequest(strategy: "FixedSize"));

        result.Success.Should().BeTrue();
        await _ingestionQueue.Received(1).EnqueueAsync(
            Arg.Is<IngestionJob>(j => j.Options.Strategy == ChunkingStrategy.FixedSize),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_InfersContentType_WhenNull()
    {
        var result = await _sut.UploadAsync(MakeRequest(fileName: "doc.pdf"));

        result.Success.Should().BeTrue();
        await _connector.Received(1).WriteFileAsync(
            Arg.Any<string>(), Arg.Any<Stream>(), "application/pdf", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_UsesExplicitContentType_WhenProvided()
    {
        var request = new UploadRequest(ContainerId, "data.txt", new MemoryStream("hello"u8.ToArray()),
            UserId, ContentType: "text/csv", IngestedVia: "API");

        var result = await _sut.UploadAsync(request);

        result.Success.Should().BeTrue();
        await _connector.Received(1).WriteFileAsync(
            Arg.Any<string>(), Arg.Any<Stream>(), "text/csv", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_LogsAuditEvent()
    {
        var result = await _sut.UploadAsync(MakeRequest());

        result.Success.Should().BeTrue();
        await _auditLogger.Received(1).LogAsync("doc.uploaded",
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    // --- Bulk upload ---

    [Fact]
    public async Task BulkUploadAsync_ProcessesAllFiles_CollectsPartialFailures()
    {
        _fileTypeValidator.IsSupported("good.txt").Returns(true);
        _fileTypeValidator.IsSupported("bad.xyz").Returns(false);

        var request = new BulkUploadRequest(ContainerId, new[]
        {
            new UploadRequest(ContainerId, "good.txt", new MemoryStream("hello"u8.ToArray()), UserId, IngestedVia: "MCP"),
            new UploadRequest(ContainerId, "bad.xyz", new MemoryStream("hello"u8.ToArray()), UserId, IngestedVia: "MCP"),
        });

        var result = await _sut.BulkUploadAsync(request);

        result.SuccessCount.Should().Be(1);
        result.FailureCount.Should().Be(1);
        result.BatchId.Should().NotBeNullOrEmpty();
        result.Results.Should().HaveCount(2);
        result.Results[0].Success.Should().BeTrue();
        result.Results[1].Success.Should().BeFalse();
    }

    [Fact]
    public async Task BulkUploadAsync_SharesBatchId()
    {
        var request = new BulkUploadRequest(ContainerId, new[]
        {
            new UploadRequest(ContainerId, "a.txt", new MemoryStream("a"u8.ToArray()), UserId, IngestedVia: "MCP"),
            new UploadRequest(ContainerId, "b.txt", new MemoryStream("b"u8.ToArray()), UserId, IngestedVia: "MCP"),
        });

        var result = await _sut.BulkUploadAsync(request);

        result.SuccessCount.Should().Be(2);
        await _ingestionQueue.Received(2).EnqueueAsync(
            Arg.Is<IngestionJob>(j => j.BatchId == result.BatchId),
            Arg.Any<CancellationToken>());
    }
}
