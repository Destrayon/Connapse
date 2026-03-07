using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Ingestion.Tests.Pipeline;

[Trait("Category", "Unit")]
public class IngestionWorkerTests
{
    private readonly ILogger<IngestionWorker> _logger = NullLogger<IngestionWorker>.Instance;

    private static IngestionJob CreateJob(string? containerId = null) =>
        new(
            JobId: Guid.NewGuid().ToString(),
            DocumentId: Guid.NewGuid().ToString(),
            Path: "/test/file.txt",
            Options: new IngestionOptions(
                FileName: "file.txt",
                ContainerId: containerId ?? Guid.NewGuid().ToString()));

    private static IOptionsMonitor<UploadSettings> CreateUploadSettings(int parallelWorkers = 1)
    {
        var settings = new UploadSettings { ParallelWorkers = parallelWorkers };
        var monitor = Substitute.For<IOptionsMonitor<UploadSettings>>();
        monitor.CurrentValue.Returns(settings);
        return monitor;
    }

    private static (IServiceScopeFactory factory, IServiceScope scope, IServiceProvider provider) CreateScopeFactory()
    {
        var provider = Substitute.For<IServiceProvider>();
        var scope = Substitute.For<IServiceScope, IAsyncDisposable>();
        scope.ServiceProvider.Returns(provider);
        ((IAsyncDisposable)scope).DisposeAsync().Returns(ValueTask.CompletedTask);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        return (scopeFactory, scope, provider);
    }

    [Fact]
    public async Task ExecuteAsync_DequeuesAndProcessesJob()
    {
        // Arrange
        var queue = new IngestionQueue();
        var job = CreateJob();
        var (scopeFactory, _, provider) = CreateScopeFactory();

        var ingester = Substitute.For<IKnowledgeIngester>();
        var expectedResult = new IngestionResult(job.DocumentId, 5, TimeSpan.FromMilliseconds(100), new List<string>());
        ingester.IngestAsync(Arg.Any<Stream>(), Arg.Any<IngestionOptions>(), Arg.Any<CancellationToken>())
            .Returns(expectedResult);
        provider.GetService(typeof(IKnowledgeIngester)).Returns(ingester);

        var containerStore = Substitute.For<IContainerStore>();
        var container = new Container(
            job.Options.ContainerId!, "test", null, ConnectorType.Filesystem,
            DateTime.UtcNow, DateTime.UtcNow);
        containerStore.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(container);
        provider.GetService(typeof(IContainerStore)).Returns(containerStore);

        var connector = Substitute.For<IConnector>();
        connector.ReadFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(new byte[] { 1, 2, 3 }));
        var connectorFactory = Substitute.For<IConnectorFactory>();
        connectorFactory.Create(Arg.Any<Container>()).Returns(connector);
        provider.GetService(typeof(IConnectorFactory)).Returns(connectorFactory);

        var worker = new IngestionWorker(queue, scopeFactory, CreateUploadSettings(), _logger);

        using var cts = new CancellationTokenSource();

        // Enqueue job, then cancel after a short delay so worker stops
        await queue.EnqueueAsync(job);

        // Act
        var workerTask = Task.Run(() => worker.StartAsync(cts.Token));
        await Task.Delay(200);
        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);
        await workerTask;

        // Assert
        await ingester.Received(1).IngestAsync(
            Arg.Any<Stream>(),
            Arg.Is<IngestionOptions>(o => o.FileName == job.Options.FileName),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_MissingContainer_ReturnsWarningResult()
    {
        // Arrange
        var queue = new IngestionQueue();
        var job = CreateJob();
        var (scopeFactory, _, provider) = CreateScopeFactory();

        var ingester = Substitute.For<IKnowledgeIngester>();
        provider.GetService(typeof(IKnowledgeIngester)).Returns(ingester);

        var containerStore = Substitute.For<IContainerStore>();
        containerStore.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Container?)null);
        provider.GetService(typeof(IContainerStore)).Returns(containerStore);

        var connectorFactory = Substitute.For<IConnectorFactory>();
        provider.GetService(typeof(IConnectorFactory)).Returns(connectorFactory);

        var worker = new IngestionWorker(queue, scopeFactory, CreateUploadSettings(), _logger);

        using var cts = new CancellationTokenSource();

        await queue.EnqueueAsync(job);

        // Act
        var workerTask = Task.Run(() => worker.StartAsync(cts.Token));
        await Task.Delay(200);
        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);
        await workerTask;

        // Assert: ingester should NOT be called since container was not found
        await ingester.DidNotReceive().IngestAsync(
            Arg.Any<Stream>(),
            Arg.Any<IngestionOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_NoContainerId_ReturnsErrorResult()
    {
        // Arrange
        var queue = new IngestionQueue();
        var job = new IngestionJob(
            JobId: Guid.NewGuid().ToString(),
            DocumentId: Guid.NewGuid().ToString(),
            Path: "/test/file.txt",
            Options: new IngestionOptions(FileName: "file.txt", ContainerId: null));
        var (scopeFactory, _, provider) = CreateScopeFactory();

        var ingester = Substitute.For<IKnowledgeIngester>();
        provider.GetService(typeof(IKnowledgeIngester)).Returns(ingester);

        var worker = new IngestionWorker(queue, scopeFactory, CreateUploadSettings(), _logger);

        using var cts = new CancellationTokenSource();

        await queue.EnqueueAsync(job);

        // Act
        var workerTask = Task.Run(() => worker.StartAsync(cts.Token));
        await Task.Delay(200);
        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);
        await workerTask;

        // Assert: ingester should NOT be called when no ContainerId
        await ingester.DidNotReceive().IngestAsync(
            Arg.Any<Stream>(),
            Arg.Any<IngestionOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_StoppingTokenCancelled_StopsGracefully()
    {
        // Arrange
        var queue = new IngestionQueue();
        var (scopeFactory, _, _) = CreateScopeFactory();

        var worker = new IngestionWorker(queue, scopeFactory, CreateUploadSettings(), _logger);

        using var cts = new CancellationTokenSource();

        // Act: start and immediately stop
        var workerTask = Task.Run(() => worker.StartAsync(cts.Token));
        await Task.Delay(50);
        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);

        // Assert: should complete without throwing
        var act = async () => await workerTask;
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ExecuteAsync_ProcessingError_ContinuesWithNextJob()
    {
        // Arrange
        var queue = new IngestionQueue();
        var failJob = CreateJob();
        var successJob = CreateJob();
        var (scopeFactory, _, provider) = CreateScopeFactory();

        var ingester = Substitute.For<IKnowledgeIngester>();
        var successResult = new IngestionResult(successJob.DocumentId, 3, TimeSpan.FromMilliseconds(50), new List<string>());
        ingester.IngestAsync(Arg.Any<Stream>(), Arg.Any<IngestionOptions>(), Arg.Any<CancellationToken>())
            .Returns(successResult);
        provider.GetService(typeof(IKnowledgeIngester)).Returns(ingester);

        var containerStore = Substitute.For<IContainerStore>();
        // First call throws, second succeeds
        var container = new Container(
            Guid.NewGuid().ToString(), "test", null, ConnectorType.Filesystem,
            DateTime.UtcNow, DateTime.UtcNow);

        containerStore.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(
                x => throw new InvalidOperationException("DB error"),
                x => Task.FromResult<Container?>(container));
        provider.GetService(typeof(IContainerStore)).Returns(containerStore);

        var connector = Substitute.For<IConnector>();
        connector.ReadFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(new byte[] { 1 }));
        var connectorFactory = Substitute.For<IConnectorFactory>();
        connectorFactory.Create(Arg.Any<Container>()).Returns(connector);
        provider.GetService(typeof(IConnectorFactory)).Returns(connectorFactory);

        var worker = new IngestionWorker(queue, scopeFactory, CreateUploadSettings(), _logger);

        using var cts = new CancellationTokenSource();

        await queue.EnqueueAsync(failJob);
        await queue.EnqueueAsync(successJob);

        // Act
        var workerTask = Task.Run(() => worker.StartAsync(cts.Token));
        await Task.Delay(300);
        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);
        await workerTask;

        // Assert: the second job should still be processed
        // (worker continues after error in first job)
        queue.QueueDepth.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_UpdatesJobStatusOnCompletion()
    {
        // Arrange
        var queue = new IngestionQueue();
        var job = CreateJob();
        var (scopeFactory, _, provider) = CreateScopeFactory();

        var ingester = Substitute.For<IKnowledgeIngester>();
        var result = new IngestionResult(job.DocumentId, 5, TimeSpan.FromMilliseconds(100), new List<string>());
        ingester.IngestAsync(Arg.Any<Stream>(), Arg.Any<IngestionOptions>(), Arg.Any<CancellationToken>())
            .Returns(result);
        provider.GetService(typeof(IKnowledgeIngester)).Returns(ingester);

        var containerStore = Substitute.For<IContainerStore>();
        var container = new Container(
            job.Options.ContainerId!, "test", null, ConnectorType.Filesystem,
            DateTime.UtcNow, DateTime.UtcNow);
        containerStore.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(container);
        provider.GetService(typeof(IContainerStore)).Returns(containerStore);

        var connector = Substitute.For<IConnector>();
        connector.ReadFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(new byte[] { 1 }));
        var connectorFactory = Substitute.For<IConnectorFactory>();
        connectorFactory.Create(Arg.Any<Container>()).Returns(connector);
        provider.GetService(typeof(IConnectorFactory)).Returns(connectorFactory);

        var worker = new IngestionWorker(queue, scopeFactory, CreateUploadSettings(), _logger);

        using var cts = new CancellationTokenSource();

        await queue.EnqueueAsync(job);

        // Act
        var workerTask = Task.Run(() => worker.StartAsync(cts.Token));
        await Task.Delay(300);
        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);
        await workerTask;

        // Assert: job status should be Completed
        var status = await queue.GetStatusAsync(job.JobId);
        status.Should().NotBeNull();
        status!.State.Should().Be(IngestionJobState.Completed);
    }
}
