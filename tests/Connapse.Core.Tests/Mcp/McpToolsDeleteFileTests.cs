using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Mcp;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Connapse.Core.Tests.Mcp;

/// <summary>
/// Unit tests for <see cref="McpTools.DeleteFile"/>.
/// Verifies happy-path deletion and partial-failure logging when
/// storage deletion fails after the DB record is removed.
/// </summary>
[Trait("Category", "Unit")]
public class McpToolsDeleteFileTests
{
    private static readonly Guid ContainerId = Guid.NewGuid();
    private const string FileId = "file-1";
    private const string FileName = "test.txt";
    private const string FilePath = "/docs/test.txt";

    private readonly IContainerStore _containerStore;
    private readonly IDocumentStore _documentStore;
    private readonly IFolderStore _folderStore;
    private readonly IKnowledgeFileSystem _fileSystem;
    private readonly ILogger<McpTools> _logger;
    private readonly IIngestionQueue _ingestionQueue;
    private readonly IServiceProvider _services;

    public McpToolsDeleteFileTests()
    {
        _containerStore = Substitute.For<IContainerStore>();
        _documentStore = Substitute.For<IDocumentStore>();
        _folderStore = Substitute.For<IFolderStore>();
        _fileSystem = Substitute.For<IKnowledgeFileSystem>();
        _logger = Substitute.For<ILogger<McpTools>>();
        _ingestionQueue = Substitute.For<IIngestionQueue>();

        _containerStore
            .GetAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(MakeContainer());

        _documentStore
            .GetAsync(FileId, Arg.Any<CancellationToken>())
            .Returns(MakeDocument());

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IContainerStore)).Returns(_containerStore);
        services.GetService(typeof(IDocumentStore)).Returns(_documentStore);
        services.GetService(typeof(IFolderStore)).Returns(_folderStore);
        services.GetService(typeof(IKnowledgeFileSystem)).Returns(_fileSystem);
        services.GetService(typeof(ILogger<McpTools>)).Returns(_logger);
        services.GetService(typeof(IIngestionQueue)).Returns(_ingestionQueue);
        _services = services;
    }

    [Fact]
    public async Task DeleteFile_StorageSucceeds_ReturnsSuccessMessage()
    {
        var result = await McpTools.DeleteFile(_services, ContainerId.ToString(), FileId);

        result.Should().Be($"File '{FileName}' (ID: {FileId}) deleted.");
        await _documentStore.Received(1).DeleteAsync(FileId, Arg.Any<CancellationToken>());
        await _fileSystem.Received(1).DeleteAsync(FilePath, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteFile_StorageFails_ReturnsPartialFailureAndLogsWarning()
    {
        _fileSystem
            .DeleteAsync(FilePath, Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("disk error"));

        var result = await McpTools.DeleteFile(_services, ContainerId.ToString(), FileId);

        result.Should().Contain("deleted from database");
        result.Should().Contain("backing storage file could not be removed");
        await _documentStore.Received(1).DeleteAsync(FileId, Arg.Any<CancellationToken>());
        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Is<IOException>(ex => ex.Message == "disk error"),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task DeleteFile_CancelsIngestionJobBeforeDeletion()
    {
        await McpTools.DeleteFile(_services, ContainerId.ToString(), FileId);

        await _ingestionQueue.Received(1).CancelJobForDocumentAsync(FileId);
        Received.InOrder(() =>
        {
            _ingestionQueue.CancelJobForDocumentAsync(FileId);
            _documentStore.DeleteAsync(FileId, Arg.Any<CancellationToken>());
        });
    }

    private static Container MakeContainer() => new(
        Id: ContainerId.ToString(),
        Name: "test",
        Description: null,
        ConnectorType: ConnectorType.ManagedStorage,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow);

    private static Document MakeDocument() => new(
        Id: FileId,
        ContainerId: ContainerId.ToString(),
        FileName: FileName,
        ContentType: "text/plain",
        Path: FilePath,
        SizeBytes: 100,
        CreatedAt: DateTime.UtcNow,
        Metadata: new());
}
