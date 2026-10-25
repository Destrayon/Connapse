using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Web.Services;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Services;

[Trait("Category", "Unit")]
public class UploadServiceTests
{
    private static readonly Guid ContainerId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly IContainerStore _containerStore = Substitute.For<IContainerStore>();
    private readonly IManagedStorageProvider _managedStorage = Substitute.For<IManagedStorageProvider>();
    private readonly IWritableConnector _connector = Substitute.For<IWritableConnector>();
    private readonly IFolderStore _folderStore = Substitute.For<IFolderStore>();
    private readonly IIngestionQueue _ingestionQueue = Substitute.For<IIngestionQueue>();
    private readonly IDocumentStore _documentStore = Substitute.For<IDocumentStore>();
    private readonly IFileTypeValidator _fileTypeValidator = Substitute.For<IFileTypeValidator>();
    private readonly IAuditLogger _auditLogger = Substitute.For<IAuditLogger>();

    private readonly IUploadService _sut;

    public UploadServiceTests()
    {
        var container = MakeContainer();
        _containerStore.GetAsync(ContainerId, Arg.Any<CancellationToken>()).Returns(container);
        _managedStorage.CreateConnector(Arg.Any<string>()).Returns(_connector);
        _connector.ResolveJobPath(Arg.Any<string>()).Returns(ci => "/" + ci.ArgAt<string>(0).TrimStart('/'));
        _fileTypeValidator.IsSupported(Arg.Any<string>()).Returns(true);
        _fileTypeValidator.SupportedExtensions.Returns(new HashSet<string> { ".txt", ".pdf", ".md" });
        _folderStore.ExistsAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        _folderStore.EnsureAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new Folder(Guid.NewGuid().ToString(), ci.ArgAt<Guid>(0).ToString(), ci.ArgAt<string>(1), DateTime.UtcNow));
        _documentStore.StoreAsync(Arg.Any<Document>(), Arg.Any<CancellationToken>())
            .Returns(ci => new StoreResult(Guid.NewGuid().ToString(), 1));
        _documentStore.StoreInFolderAsync(Arg.Any<Document>(), Arg.Any<Guid>(), Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                await ci.ArgAt<Func<CancellationToken, Task>>(2)(CancellationToken.None);
                return (StoreResult?)new StoreResult(Guid.NewGuid().ToString(), 1);
            });
        _ingestionQueue.EnqueueAsync(Arg.Any<IngestionJob>(), Arg.Any<CancellationToken>())
            .Returns(ci => Guid.NewGuid().ToString());

        _sut = new UploadService(
            _containerStore, _managedStorage, _folderStore,
            _ingestionQueue, _documentStore, _fileTypeValidator, _auditLogger,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UploadService>.Instance);
    }

    private UploadRequest MakeRequest(
        string fileName = "test.txt",
        byte[]? content = null,
        string? path = null,
        string? strategy = null) =>
        new(ContainerId, fileName, new MemoryStream(content ?? "hello"u8.ToArray()),
            UserId, path, null, strategy, "API");

    private static Container MakeContainer() =>
        new(ContainerId.ToString(), "test", null, DateTime.UtcNow, DateTime.UtcNow);

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
    public async Task UploadAsync_RejectsFilenameLongerThan255Characters()
    {
        var longName = new string('a', 252) + ".txt"; // 256 chars total
        var result = await _sut.UploadAsync(MakeRequest(fileName: longName));
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("255");
    }

    [Fact]
    public async Task UploadAsync_AcceptsFilenameAtExactly255Characters()
    {
        var exactName = new string('a', 251) + ".txt"; // 255 chars total
        var result = await _sut.UploadAsync(MakeRequest(fileName: exactName));
        result.Success.Should().BeTrue();
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
    public async Task UploadAsync_RejectsNonWritableConnector()
    {
        // Write access is a type guarantee as of #351: a connector that is not an
        // IWritableConnector cannot be written to, so there is no runtime flag to unset.
        _managedStorage.CreateConnector(Arg.Any<string>()).Returns(Substitute.For<IConnector>());

        var result = await _sut.UploadAsync(MakeRequest());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not writable");
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
        var folder = new Folder(Guid.NewGuid().ToString(), ContainerId.ToString(), "/docs/reports/", DateTime.UtcNow);
        _folderStore.EnsureAsync(ContainerId, "/docs/reports/", Arg.Any<CancellationToken>()).Returns(folder);

        var result = await _sut.UploadAsync(MakeRequest(path: "/docs/reports/"));

        result.Success.Should().BeTrue();
        Received.InOrder(() =>
        {
            // The file is written inside the store, while it holds the folder against a delete.
            _folderStore.EnsureAsync(ContainerId, "/docs/reports/", Arg.Any<CancellationToken>());
            _documentStore.StoreInFolderAsync(Arg.Any<Document>(), Guid.Parse(folder.Id), Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
            _connector.WriteFileAsync("docs/reports/test.txt", Arg.Any<Stream>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task UploadAsync_FolderDeletedBeforeTheStore_FailsHavingWrittenNothing()
    {
        _documentStore.StoreInFolderAsync(Arg.Any<Document>(), Arg.Any<Guid>(), Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns((StoreResult?)null);

        var result = await _sut.UploadAsync(MakeRequest(path: "/docs/"));

        result.Success.Should().BeFalse();
        await _connector.DidNotReceive().WriteFileAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _connector.DidNotReceive().DeleteFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _ingestionQueue.DidNotReceive().EnqueueAsync(Arg.Any<IngestionJob>(), Arg.Any<CancellationToken>());
    }

    // --- A failure after the file is written (CON-88) ---

    [Fact]
    public async Task UploadAsync_RecordFailsAfterTheWrite_RemovesTheStoredFile()
    {
        _documentStore.StoreAsync(Arg.Any<Document>(), Arg.Any<CancellationToken>())
            .Returns<StoreResult>(_ => throw new InvalidOperationException("database down"));

        var act = () => _sut.UploadAsync(MakeRequest());

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _connector.Received(1).DeleteFileAsync("test.txt", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_RecordFailsAfterTheWriteInAFolder_RemovesTheStoredFile()
    {
        _documentStore.StoreInFolderAsync(Arg.Any<Document>(), Arg.Any<Guid>(), Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns<Task<StoreResult?>>(async ci =>
            {
                await ci.ArgAt<Func<CancellationToken, Task>>(2)(CancellationToken.None);
                throw new InvalidOperationException("commit failed");
            });

        var act = () => _sut.UploadAsync(MakeRequest(path: "/docs/"));

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _connector.Received(1).DeleteFileAsync("docs/test.txt", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_CancelledAfterTheWrite_StillRemovesTheStoredFile()
    {
        using var cts = new CancellationTokenSource();
        _documentStore.StoreAsync(Arg.Any<Document>(), Arg.Any<CancellationToken>())
            .Returns<StoreResult>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        var act = () => _sut.UploadAsync(MakeRequest(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await _connector.Received(1).DeleteFileAsync("test.txt", Arg.Is<CancellationToken>(t => !t.IsCancellationRequested));
    }

    [Fact]
    public async Task UploadAsync_ReplacingADocumentFails_KeepsTheFileItsRecordPointsTo()
    {
        var existing = new Document(Guid.NewGuid().ToString(), ContainerId.ToString(), "test.txt", "text/plain", "/test.txt", 5, DateTime.UtcNow, []);
        _documentStore.GetByPathAsync(ContainerId, "/test.txt", Arg.Any<CancellationToken>()).Returns(existing);
        _documentStore.StoreAsync(Arg.Any<Document>(), Arg.Any<CancellationToken>())
            .Returns<StoreResult>(_ => throw new InvalidOperationException("database down"));

        var act = () => _sut.UploadAsync(MakeRequest());

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _connector.DidNotReceive().DeleteFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_FailsWhileAnotherUploadToThePathLands_KeepsTheOtherUploadsFile()
    {
        var winner = new Document(Guid.NewGuid().ToString(), ContainerId.ToString(), "test.txt", "text/plain", "/test.txt", 5, DateTime.UtcNow, []);
        _documentStore.GetByPathAsync(ContainerId, "/test.txt", Arg.Any<CancellationToken>()).Returns(null, winner);
        _documentStore.StoreAsync(Arg.Any<Document>(), Arg.Any<CancellationToken>())
            .Returns<StoreResult>(_ => throw new InvalidOperationException("database down"));

        var act = () => _sut.UploadAsync(MakeRequest());

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _connector.DidNotReceive().DeleteFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BulkUploadAsync_OneFileFailsAfterItsWrite_RemovesOnlyThatFile()
    {
        _documentStore.StoreAsync(Arg.Is<Document>(d => d.FileName == "b.txt"), Arg.Any<CancellationToken>())
            .Returns<StoreResult>(_ => throw new InvalidOperationException("database down"));
        var request = new BulkUploadRequest(ContainerId, new[]
        {
            new UploadRequest(ContainerId, "a.txt", new MemoryStream("a"u8.ToArray()), UserId, IngestedVia: "MCP"),
            new UploadRequest(ContainerId, "b.txt", new MemoryStream("b"u8.ToArray()), UserId, IngestedVia: "MCP"),
        });

        var result = await _sut.BulkUploadAsync(request);

        result.SuccessCount.Should().Be(1);
        await _connector.Received(1).DeleteFileAsync("b.txt", Arg.Any<CancellationToken>());
        await _connector.DidNotReceive().DeleteFileAsync("a.txt", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("FixedSize", true)]
    public async Task UploadAsync_MarksOnlyACallersChosenStrategyAsExplicit(string? strategy, bool expected)
    {
        // #633: PDF, DOCX and PPTX go to the heading-aware chunker unless the caller chose.
        var result = await _sut.UploadAsync(MakeRequest(strategy: strategy));

        result.Success.Should().BeTrue();
        await _ingestionQueue.Received(1).EnqueueAsync(
            Arg.Is<IngestionJob>(j => j.Options.StrategyIsExplicit == expected),
            Arg.Any<CancellationToken>());
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
