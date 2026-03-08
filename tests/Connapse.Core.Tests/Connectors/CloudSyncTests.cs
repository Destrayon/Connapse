using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Core.Tests.Connectors;

[Trait("Category", "Unit")]
public class CloudSyncTests
{
    private readonly IConnectorFactory _connectorFactory = Substitute.For<IConnectorFactory>();
    private readonly IIngestionQueue _queue = Substitute.For<IIngestionQueue>();
    private readonly IDocumentStore _documentStore = Substitute.For<IDocumentStore>();
    private readonly FileBrowserChangeNotifier _notifier = new();
    private readonly ConnectorWatcherService _service;

    public CloudSyncTests()
    {
        var scopeFactory = CreateScopeFactory();
        var chunkingSettings = Substitute.For<IOptionsMonitor<ChunkingSettings>>();
        chunkingSettings.CurrentValue.Returns(new ChunkingSettings { Strategy = "Recursive" });

        _service = new ConnectorWatcherService(
            scopeFactory,
            _connectorFactory,
            _queue,
            _notifier,
            chunkingSettings,
            NullLogger<ConnectorWatcherService>.Instance);
    }

    private static Container MakeS3Container(string? id = null) => new(
        Id: id ?? Guid.NewGuid().ToString(),
        Name: "s3-test",
        Description: null,
        ConnectorType: ConnectorType.S3,

        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow,
        ConnectorConfig: """{"bucketName":"test","region":"us-east-1"}""");

    private static Container MakeAzureContainer(string? id = null) => new(
        Id: id ?? Guid.NewGuid().ToString(),
        Name: "azure-test",
        Description: null,
        ConnectorType: ConnectorType.AzureBlob,

        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow,
        ConnectorConfig: """{"storageAccountName":"test","containerName":"data"}""");

    private static ConnectorFile MakeRemoteFile(string path, DateTime? lastModified = null, long size = 1024) =>
        new(path, size, lastModified ?? DateTime.UtcNow, "text/plain");

    private static Document MakeDocument(string containerId, string path, string status = "Ready") =>
        new(Guid.NewGuid().ToString(), containerId, Path.GetFileName(path), "text/plain", path, 1024, DateTime.UtcNow,
            new Dictionary<string, string> { ["Status"] = status });

    [Fact]
    public async Task CloudSync_NewRemoteFile_EnqueuesIngestion()
    {
        var container = MakeS3Container();
        var connector = Substitute.For<IConnector>();
        connector.ListFilesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([MakeRemoteFile("/docs/report.pdf")]);

        _connectorFactory.Create(container).Returns(connector);
        _documentStore.ListAsync(Guid.Parse(container.Id), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Document>());

        await _service.CloudSyncAsync(container, CancellationToken.None);

        await _queue.Received(1).EnqueueAsync(
            Arg.Is<IngestionJob>(j => j.Options.Path == "/docs/report.pdf" && j.Options.ContainerId == container.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloudSync_DeletedRemoteFile_RemovesFromDb()
    {
        var container = MakeS3Container();
        var doc = MakeDocument(container.Id, "/docs/old-file.txt");

        var connector = Substitute.For<IConnector>();
        connector.ListFilesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ConnectorFile>()); // remote is empty

        _connectorFactory.Create(container).Returns(connector);
        _documentStore.ListAsync(Guid.Parse(container.Id), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { doc });
        _documentStore.GetByPathAsync(Guid.Parse(container.Id), "/docs/old-file.txt", Arg.Any<CancellationToken>())
            .Returns(doc);

        await _service.CloudSyncAsync(container, CancellationToken.None);

        await _documentStore.Received(1).DeleteAsync(doc.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloudSync_UnchangedFile_SkipsIngestion()
    {
        var container = MakeS3Container();
        var now = DateTime.UtcNow;
        var remoteFile = MakeRemoteFile("/docs/stable.txt", now, 512);
        var doc = MakeDocument(container.Id, "/docs/stable.txt");

        var connector = Substitute.For<IConnector>();
        connector.ListFilesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { remoteFile });

        _connectorFactory.Create(container).Returns(connector);
        _documentStore.ListAsync(Guid.Parse(container.Id), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { doc });

        // First poll — builds the snapshot, but file already in DB so no enqueue
        await _service.CloudSyncAsync(container, CancellationToken.None);

        // Second poll — same file, same LastModified/Size → no enqueue
        await _service.CloudSyncAsync(container, CancellationToken.None);

        await _queue.DidNotReceive().EnqueueAsync(Arg.Any<IngestionJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloudSync_ChangedFile_ReIngestsOnSecondPoll()
    {
        var container = MakeS3Container();
        var originalTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var updatedTime = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var doc = MakeDocument(container.Id, "/docs/report.pdf");

        var connector = Substitute.For<IConnector>();
        _connectorFactory.Create(container).Returns(connector);
        _documentStore.ListAsync(Guid.Parse(container.Id), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { doc });

        // First poll — builds snapshot with originalTime
        connector.ListFilesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeRemoteFile("/docs/report.pdf", originalTime, 1024) });
        await _service.CloudSyncAsync(container, CancellationToken.None);

        // Second poll — file has a newer LastModified → should re-ingest
        connector.ListFilesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeRemoteFile("/docs/report.pdf", updatedTime, 1024) });
        await _service.CloudSyncAsync(container, CancellationToken.None);

        await _queue.Received(1).EnqueueAsync(
            Arg.Is<IngestionJob>(j => j.Options.Path == "/docs/report.pdf" && j.DocumentId == doc.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloudSync_InFlightFile_SkipsReIngestion()
    {
        var container = MakeS3Container();
        var originalTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var updatedTime = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var doc = MakeDocument(container.Id, "/docs/processing.pdf", "Processing");

        var connector = Substitute.For<IConnector>();
        _connectorFactory.Create(container).Returns(connector);
        _documentStore.ListAsync(Guid.Parse(container.Id), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { doc });

        // First poll — builds snapshot
        connector.ListFilesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeRemoteFile("/docs/processing.pdf", originalTime, 1024) });
        await _service.CloudSyncAsync(container, CancellationToken.None);

        // Second poll — file changed but is in-flight → skip
        connector.ListFilesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeRemoteFile("/docs/processing.pdf", updatedTime, 1024) });
        await _service.CloudSyncAsync(container, CancellationToken.None);

        await _queue.DidNotReceive().EnqueueAsync(Arg.Any<IngestionJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloudSync_FirstPollWithExistingReadyFile_SkipsChangeDetection()
    {
        // On first poll there's no snapshot, so we can't detect changes.
        // A "Ready" file that exists in DB should be skipped (not re-ingested).
        var container = MakeS3Container();
        var doc = MakeDocument(container.Id, "/docs/existing.txt");

        var connector = Substitute.For<IConnector>();
        connector.ListFilesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeRemoteFile("/docs/existing.txt") });

        _connectorFactory.Create(container).Returns(connector);
        _documentStore.ListAsync(Guid.Parse(container.Id), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { doc });

        await _service.CloudSyncAsync(container, CancellationToken.None);

        await _queue.DidNotReceive().EnqueueAsync(Arg.Any<IngestionJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloudSync_ConnectorCreationFails_DoesNotThrow()
    {
        var container = MakeS3Container();
        _connectorFactory.Create(container).Returns(_ => throw new InvalidOperationException("Bad config"));

        var act = () => _service.CloudSyncAsync(container, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CloudSync_ListFilesThrows_BubblesUp()
    {
        // CloudSyncAsync lets exceptions propagate (PollCloudContainerAsync catches them).
        // This verifies the caller can handle the error.
        var container = MakeS3Container();
        var connector = Substitute.For<IConnector>();
        connector.ListFilesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ConnectorFile>>(_ => throw new AggregateException("Network error"));

        _connectorFactory.Create(container).Returns(connector);

        var act = () => _service.CloudSyncAsync(container, CancellationToken.None);

        await act.Should().ThrowAsync<AggregateException>();
    }

    [Fact]
    public async Task CloudSync_AzureBlobContainer_DetectsNewFiles()
    {
        var container = MakeAzureContainer();
        var connector = Substitute.For<IConnector>();
        connector.ListFilesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeRemoteFile("/data/image.png", null, 2048) });

        _connectorFactory.Create(container).Returns(connector);
        _documentStore.ListAsync(Guid.Parse(container.Id), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Document>());

        await _service.CloudSyncAsync(container, CancellationToken.None);

        await _queue.Received(1).EnqueueAsync(
            Arg.Is<IngestionJob>(j => j.Options.Path == "/data/image.png"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloudSync_IngestionMetadata_HasCloudPollSource()
    {
        var container = MakeS3Container();
        var connector = Substitute.For<IConnector>();
        connector.ListFilesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeRemoteFile("/new-file.md") });

        _connectorFactory.Create(container).Returns(connector);
        _documentStore.ListAsync(Guid.Parse(container.Id), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Document>());

        await _service.CloudSyncAsync(container, CancellationToken.None);

        await _queue.Received(1).EnqueueAsync(
            Arg.Is<IngestionJob>(j => j.Options.Metadata!["Source"] == "CloudPoll"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloudSync_DisposableConnector_IsDisposed()
    {
        var container = MakeS3Container();
        var connector = Substitute.For<IConnector, IDisposable>();
        connector.ListFilesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ConnectorFile>());

        _connectorFactory.Create(container).Returns(connector);
        _documentStore.ListAsync(Guid.Parse(container.Id), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Document>());

        await _service.CloudSyncAsync(container, CancellationToken.None);

        ((IDisposable)connector).Received(1).Dispose();
    }

    private IServiceScopeFactory CreateScopeFactory()
    {
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IDocumentStore)).Returns(_documentStore);
        serviceProvider.GetService(typeof(IContainerStore)).Returns(Substitute.For<IContainerStore>());
        serviceProvider.GetService(typeof(IFolderStore)).Returns(Substitute.For<IFolderStore>());

        // AsyncServiceScope (used by CreateAsyncScope extension) wraps IServiceScope internally.
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        return scopeFactory;
    }
}
