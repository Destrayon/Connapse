using Connapse.Background.Storage;
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Hangfire;
using Hangfire.MemoryStorage;
using Xunit;

namespace Connapse.Background.Tests.Integration;

/// <summary>
/// End-to-end shape test for the Hangfire-backed ingestion queue.
///
/// Verifies the WIRING (HangfireIngestionQueue creates an Enqueued ingest job plus an
/// Awaiting continuation summary job in Hangfire's in-memory storage). The work inside
/// each job handler is unit-tested separately in IngestionJobsTests + SummaryJobsTests;
/// this test deliberately does not boot a BackgroundJobServer because the worker resolves
/// job handlers via the ambient DI activator and would require Postgres / a full app host.
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
    public async Task EnqueueAsync_CreatesOnlyIngestJob_NoUpfrontPerDocContinuation()
    {
        // Post-HERCULES: HangfireIngestionQueue no longer attaches a PerDocSummary
        // continuation. The per-doc job is enqueued (or skipped) inside IngestAsync's
        // tail based on the container's resolved SummarySettings, so document-clustering
        // and summaries-disabled containers don't create any per-doc job at all.
        var bgClient = new BackgroundJobClient();
        var queue = new HangfireIngestionQueue(bgClient);

        string documentId = Guid.NewGuid().ToString();
        var job = new IngestionJob(
            JobId: Guid.NewGuid().ToString(),
            DocumentId: documentId,
            Path: "/test.txt",
            Options: new IngestionOptions(
                DocumentId: documentId,
                FileName: "test.txt",
                ContentType: "text/plain"));

        await queue.EnqueueAsync(job, CancellationToken.None);

        var monitor = JobStorage.Current.GetMonitoringApi();

        var enqueuedJobs = monitor.Queues()
            .SelectMany(q => monitor.EnqueuedJobs(q.Name, 0, 100))
            .ToList();

        enqueuedJobs.Should().HaveCount(1,
            "HangfireIngestionQueue.EnqueueAsync should enqueue exactly one ingest job");
        enqueuedJobs.Single().Value.Job!.Method.Name.Should().Be(
            nameof(Connapse.Background.Jobs.IIngestionJobs.IngestAsync));

        // Verify NO continuation is attached — the per-doc decision is deferred to IngestAsync.
        string parentId = enqueuedJobs.Single().Key;
        var parentDetails = monitor.JobDetails(parentId);
        parentDetails.Should().NotBeNull();
        parentDetails.Properties.Should().NotContainKey("Continuations",
            "post-HERCULES, HangfireIngestionQueue should not attach an upfront PerDocSummary continuation");
    }
}
