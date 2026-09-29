using Connapse.Background.Storage;
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Hangfire;
using Hangfire.MemoryStorage;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Connapse.Background.Tests.Integration;

/// <summary>
/// End-to-end shape test for the Hangfire-backed ingestion queue.
///
/// Verifies the WIRING (HangfireIngestionQueue marks the document Queued, creates an Enqueued
/// ingest job carrying the new generation, and records that job on the document) in Hangfire's
/// in-memory storage. The work inside each job handler is unit-tested separately in
/// IngestionJobsTests + SummaryJobsTests; this test deliberately does not boot a
/// BackgroundJobServer because the worker resolves job handlers via the ambient DI activator
/// and would require Postgres / a full app host.
/// </summary>
[Trait("Category", "Integration")]
public sealed class HangfireEndToEndTests
{
    public HangfireEndToEndTests()
    {
        // JobStorage.Current is a process-global static. Re-applying MemoryStorage on every
        // test instantiation is idempotent and ensures previous unit-test runs in the same
        // process haven't left a different backend active.
        GlobalConfiguration.Configuration
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseMemoryStorage();
    }

    [Fact]
    public async Task EnqueueAsync_MarksQueuedThenEnqueuesOneIngestJobAtTheNewGeneration()
    {
        Guid documentId = Guid.NewGuid();
        var lifecycle = Substitute.For<IDocumentLifecycle>();
        lifecycle.EnqueuedAsync(documentId, true, Arg.Any<CancellationToken>()).Returns(42);

        var services = new ServiceCollection();
        services.AddSingleton(lifecycle);
        await using var provider = services.BuildServiceProvider();

        var queue = new HangfireIngestionQueue(
            new BackgroundJobClient(), provider.GetRequiredService<IServiceScopeFactory>());

        string? jobId = await queue.EnqueueAsync(new IngestionJob(
            DocumentId: documentId.ToString(),
            Options: new IngestionOptions(
                DocumentId: documentId.ToString(),
                FileName: "test.txt",
                ContentType: "text/plain")), CancellationToken.None);

        var monitor = JobStorage.Current.GetMonitoringApi();
        var details = monitor.JobDetails(jobId);

        details.Should().NotBeNull();
        details.Job!.Method.Name.Should().Be(nameof(Connapse.Background.Jobs.IIngestionJobs.IngestAsync));
        details.Job.Args[1].Should().BeOfType<IngestionOptions>()
            .Which.Generation.Should().Be(42, "the worker must be able to tell it is still the latest");
        details.Properties.Should().NotContainKey("Continuations",
            "the per-doc summary is decided inside IngestAsync, not attached up front");

        await lifecycle.Received(1).RecordJobAsync(documentId, 42, jobId!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnqueueAsync_DocumentMissing_EnqueuesNothing()
    {
        var lifecycle = Substitute.For<IDocumentLifecycle>();
        lifecycle.EnqueuedAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns((int?)null);

        var services = new ServiceCollection();
        services.AddSingleton(lifecycle);
        await using var provider = services.BuildServiceProvider();

        var bgClient = Substitute.For<IBackgroundJobClient>();
        var queue = new HangfireIngestionQueue(bgClient, provider.GetRequiredService<IServiceScopeFactory>());

        string documentId = Guid.NewGuid().ToString();
        string? jobId = await queue.EnqueueAsync(
            new IngestionJob(documentId, new IngestionOptions(DocumentId: documentId)), CancellationToken.None);

        jobId.Should().BeNull();
        bgClient.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }
}
