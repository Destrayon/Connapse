using System.Diagnostics;
using Connapse.Background.Jobs;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Vectors;
using FluentAssertions;
using Hangfire;
using Hangfire.MemoryStorage;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Connapse.Background.Tests.Integration;

/// <summary>
/// CON-354: moving large documents to their own lane must not slow them down. The embedding throttle (2 requests) is
/// the bottleneck either way, so the large lane's 2 workers keep it as busy as the old 4 shared workers did. Each
/// document embeds through the real throttle against a provider with a fixed delay per request.
/// </summary>
[Trait("Category", "Gate")]
public sealed class LargeDocumentThroughputGateTests
{
    private const int Documents = 4;
    private const int RequestsPerDocument = 20;

    [Fact]
    public async Task LargeDocuments_OnTheLargeLane_IndexAsFastAsOnTheSharedQueue()
    {
        TimeSpan before = await TimeLargeDocumentsAsync(largeLane: false);
        TimeSpan after = await TimeLargeDocumentsAsync(largeLane: true);

        Console.WriteLine($"CON-354 throughput: shared queue {before.TotalMilliseconds:F0} ms, large lane {after.TotalMilliseconds:F0} ms");
        after.Should().BeLessThan(before * 1.2, "the large lane must not slow large documents down");
    }

    private static async Task<TimeSpan> TimeLargeDocumentsAsync(bool largeLane)
    {
        var settings = new EmbeddingSettings { BatchSize = 16, MaxConcurrentIngestionRequests = 2 };
        var monitor = Substitute.For<IOptionsMonitor<EmbeddingSettings>>();
        monitor.CurrentValue.Returns(settings);
        var embedder = new ThrottledEmbeddingProvider(
            new DelayedProvider(TimeSpan.FromMilliseconds(25)), new EmbeddingThrottle(monitor));
        var work = new EmbeddingIngestion(embedder, RequestsPerDocument * settings.BatchSize);

        IngestionLanes lanes = new(IngestionLanes.DefaultWorkers);
        var storage = new MemoryStorage();
        IEnumerable<BackgroundJobServerOptions> options = largeLane
            ? lanes.ServerOptions("gate")
            : [new BackgroundJobServerOptions { WorkerCount = lanes.Workers, Queues = [JobQueues.Ingestion, JobQueues.Default] }];
        var servers = options
            .Select(o => new BackgroundJobServer(
                new BackgroundJobServerOptions
                {
                    WorkerCount = o.WorkerCount,
                    Queues = o.Queues,
                    ServerName = o.ServerName,
                    Activator = new Activator(work),
                },
                storage))
            .ToList();

        try
        {
            var client = new BackgroundJobClient(storage);
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < Documents; i++)
            {
                string id = Guid.NewGuid().ToString();
                if (largeLane)
                    client.Enqueue<IIngestionJobs>(j => j.IngestLargeAsync(id, new IngestionOptions(), CancellationToken.None));
                else
                    client.Enqueue<IIngestionJobs>(j => j.IngestAsync(id, new IngestionOptions(), CancellationToken.None));
            }
            await work.AllFinished.WaitAsync(TimeSpan.FromSeconds(60));
            return clock.Elapsed;
        }
        finally
        {
            foreach (BackgroundJobServer server in servers)
                server.Dispose();
        }
    }

    /// <summary>Each document embeds its texts in one call, as semantic chunking does.</summary>
    private sealed class EmbeddingIngestion(IEmbeddingProvider embedder, int textsPerDocument) : IIngestionJobs
    {
        private readonly TaskCompletionSource _allFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _finished;

        public Task AllFinished => _allFinished.Task;

        public Task IngestAsync(string documentId, IngestionOptions options, CancellationToken ct) => RunAsync(ct);

        public Task IngestLargeAsync(string documentId, IngestionOptions options, CancellationToken ct) => RunAsync(ct);

        public Task RequeueStuckDocumentsAsync(CancellationToken ct) => Task.CompletedTask;

        private async Task RunAsync(CancellationToken ct)
        {
            await embedder.EmbedBatchAsync(
                Enumerable.Range(0, textsPerDocument).Select(i => $"s{i}"), EmbeddingInputType.Document, ct);
            if (Interlocked.Increment(ref _finished) == Documents)
                _allFinished.TrySetResult();
        }
    }

    /// <summary>Stands in for a CPU-bound embedding model: every request takes the same time.</summary>
    private sealed class DelayedProvider(TimeSpan perRequest) : IEmbeddingProvider
    {
        public int Dimensions => 1;
        public string ModelId => "delayed";

        public Task<float[]> EmbedAsync(string text, EmbeddingInputType inputType, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
            IEnumerable<string> texts, EmbeddingInputType inputType, CancellationToken ct = default)
        {
            List<string> batch = texts.ToList();
            await Task.Delay(perRequest, ct);
            return batch.Select(_ => new float[1]).ToList();
        }
    }

    private sealed class Activator(IIngestionJobs jobs) : JobActivator
    {
        public override object ActivateJob(Type jobType) =>
            jobType == typeof(IIngestionJobs) ? jobs : base.ActivateJob(jobType);
    }
}
