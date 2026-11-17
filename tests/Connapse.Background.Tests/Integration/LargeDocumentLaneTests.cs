using System.Collections.Concurrent;
using System.Diagnostics;
using Connapse.Background.Jobs;
using Connapse.Background.Storage;
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Hangfire;
using Hangfire.MemoryStorage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Connapse.Background.Tests.Integration;

/// <summary>
/// CON-354: a bulk upload of large PDFs took every ingestion worker for over an hour while other
/// uploads waited at 0 chunks. These tests boot the Hangfire servers production configures, enqueue
/// through the production queue, and stand in only for the ingestion work itself.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LargeDocumentLaneTests
{
    /// <summary>
    /// The bar, set before the fix: while large documents occupy every worker they can take, a small
    /// upload is picked up and finished within this long.
    /// </summary>
    private static readonly TimeSpan SmallUploadBar = TimeSpan.FromSeconds(15);

    private const long LargeBytes = 40L * 1024 * 1024;
    private const long SmallBytes = 20L * 1024;
    private const int SmallUploads = 5;

    [Fact]
    public async Task SmallUploads_WhileLargeDocumentsIndex_FinishWithinTheBar()
    {
        IngestionLanes lanes = IngestionLanes.FromConfiguration(new ConfigurationBuilder().Build());
        var storage = new MemoryStorage();
        var work = new FakeIngestion();

        var sizes = new ConcurrentDictionary<Guid, long>();
        var documents = Substitute.For<IDocumentStore>();
        documents.StoredByteSizeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => sizes.TryGetValue(Guid.Parse(call.Arg<string>()), out long bytes) ? bytes : (long?)null);
        var lifecycle = Substitute.For<IDocumentLifecycle>();
        lifecycle.EnqueuedAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(1);
        lifecycle.RecordJobAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var services = new ServiceCollection();
        services.AddSingleton(documents);
        services.AddSingleton(lifecycle);
        await using var provider = services.BuildServiceProvider();
        var queue = new HangfireIngestionQueue(
            new BackgroundJobClient(storage), provider.GetRequiredService<IServiceScopeFactory>(), lanes);

        var servers = lanes.ServerOptions("test")
            .Select(o => new BackgroundJobServer(
                new BackgroundJobServerOptions
                {
                    WorkerCount = o.WorkerCount,
                    Queues = o.Queues,
                    ServerName = o.ServerName,
                    Activator = new FakeActivator(work),
                    SchedulePollingInterval = TimeSpan.FromSeconds(1),
                },
                storage))
            .ToList();

        try
        {
            // As many large documents as there are workers of any kind: before CON-354 they took all of them.
            int largeCount = lanes.ServerOptions("test").Sum(o => o.WorkerCount);
            for (int i = 0; i < largeCount; i++)
                await EnqueueAsync(queue, sizes, work.Large, LargeBytes);

            await work.WaitForLargeStartedAsync(Math.Min(largeCount, lanes.LargeWorkers), TimeSpan.FromSeconds(30));

            var small = new List<Guid>();
            for (int i = 0; i < SmallUploads; i++)
                small.Add(await EnqueueAsync(queue, sizes, null, SmallBytes));

            var clock = Stopwatch.StartNew();
            await work.WaitForSmallFinishedAsync(small, SmallUploadBar);
            clock.Elapsed.Should().BeLessThan(SmallUploadBar);
        }
        finally
        {
            work.ReleaseLarge();
            foreach (BackgroundJobServer server in servers)
                server.Dispose();
        }
    }

    [Fact]
    public async Task EnqueueAsync_RoutesBySizeAgainstTheThreshold()
    {
        IngestionLanes lanes = new(Workers: 4, LargeWorkers: 2, LargeDocumentBytes: 1000);
        var storage = new MemoryStorage();
        var sizes = new ConcurrentDictionary<Guid, long>();
        var documents = Substitute.For<IDocumentStore>();
        documents.StoredByteSizeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => sizes.TryGetValue(Guid.Parse(call.Arg<string>()), out long bytes) ? bytes : (long?)null);
        var lifecycle = Substitute.For<IDocumentLifecycle>();
        lifecycle.EnqueuedAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(1);

        var services = new ServiceCollection();
        services.AddSingleton(documents);
        services.AddSingleton(lifecycle);
        await using var provider = services.BuildServiceProvider();
        var queue = new HangfireIngestionQueue(
            new BackgroundJobClient(storage), provider.GetRequiredService<IServiceScopeFactory>(), lanes);

        var monitor = storage.GetMonitoringApi();
        string Queue(string jobId) => monitor.JobDetails(jobId).History
            .First(h => h.StateName == Hangfire.States.EnqueuedState.StateName).Data["Queue"];

        Guid under = Guid.NewGuid(); sizes[under] = 999;
        Guid at = Guid.NewGuid(); sizes[at] = 1000;
        string underJob = (await queue.EnqueueAsync(new IngestionJob(under.ToString(), new IngestionOptions())))!;
        string atJob = (await queue.EnqueueAsync(new IngestionJob(at.ToString(), new IngestionOptions())))!;

        Queue(underJob).Should().Be(JobQueues.Ingestion);
        Queue(atJob).Should().Be(JobQueues.IngestionLarge);

        // A source sync knows the new version's size; the stored one is still the last version's.
        Guid grown = Guid.NewGuid(); sizes[grown] = 10;
        string grownJob = (await queue.EnqueueAsync(new IngestionJob(grown.ToString(), new IngestionOptions(), SizeBytes: 5000)))!;
        Queue(grownJob).Should().Be(JobQueues.IngestionLarge);

        // No size known to be a byte count, as for a Confluence page not yet ingested: the general lane.
        Guid unknown = Guid.NewGuid();
        string unknownJob = (await queue.EnqueueAsync(new IngestionJob(unknown.ToString(), new IngestionOptions())))!;
        Queue(unknownJob).Should().Be(JobQueues.Ingestion);
    }

    private static async Task<Guid> EnqueueAsync(
        HangfireIngestionQueue queue, ConcurrentDictionary<Guid, long> sizes, ConcurrentDictionary<Guid, byte>? large, long bytes)
    {
        Guid id = Guid.NewGuid();
        sizes[id] = bytes;
        large?.TryAdd(id, 0);
        string? jobId = await queue.EnqueueAsync(new IngestionJob(id.ToString(), new IngestionOptions(DocumentId: id.ToString())));
        jobId.Should().NotBeNull();
        return id;
    }

    /// <summary>Large documents run until released; small ones finish at once.</summary>
    private sealed class FakeIngestion : IIngestionJobs
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _finished = new();
        private int _largeStarted;

        public ConcurrentDictionary<Guid, byte> Large { get; } = new();

        public Task IngestAsync(string documentId, IngestionOptions options, CancellationToken ct) => RunAsync(documentId, ct);

        public Task IngestLargeAsync(string documentId, IngestionOptions options, CancellationToken ct) => RunAsync(documentId, ct);

        public Task RequeueStuckDocumentsAsync(CancellationToken ct) => Task.CompletedTask;

        private async Task RunAsync(string documentId, CancellationToken ct)
        {
            Guid id = Guid.Parse(documentId);
            if (Large.ContainsKey(id))
            {
                Interlocked.Increment(ref _largeStarted);
                await _release.Task.WaitAsync(ct);
                return;
            }
            Finished(id).TrySetResult();
        }

        private TaskCompletionSource Finished(Guid id) =>
            _finished.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public void ReleaseLarge() => _release.TrySetResult();

        public async Task WaitForLargeStartedAsync(int count, TimeSpan timeout)
        {
            var clock = Stopwatch.StartNew();
            while (Volatile.Read(ref _largeStarted) < count)
            {
                clock.Elapsed.Should().BeLessThan(timeout, "the large documents should have started");
                await Task.Delay(100);
            }
        }

        public async Task WaitForSmallFinishedAsync(IEnumerable<Guid> ids, TimeSpan timeout)
        {
            Func<Task> all = () => Task.WhenAll(ids.Select(id => Finished(id).Task)).WaitAsync(timeout);
            await all.Should().NotThrowAsync("small uploads must not wait behind large documents");
        }
    }

    private sealed class FakeActivator(IIngestionJobs jobs) : JobActivator
    {
        public override object ActivateJob(Type jobType) =>
            jobType == typeof(IIngestionJobs) ? jobs : base.ActivateJob(jobType);
    }
}
