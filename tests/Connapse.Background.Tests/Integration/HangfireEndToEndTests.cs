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
    public async Task EnqueueAsync_CreatesEnqueuedIngestJobAndAwaitingSummaryContinuation()
    {
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

        // Hangfire reads the [Queue] attribute from the resolved Job.Method. When the
        // method is invoked via an interface expression (Enqueue<IIngestionJobs>), the
        // attribute lookup may resolve via the interface declaration — which has no
        // [Queue] — so jobs can land on "default" rather than "ingestion". In production
        // the worker is configured to listen on both queues; for this wiring test we
        // collect across whatever queue Hangfire picked.
        var enqueuedJobs = monitor.Queues()
            .SelectMany(q => monitor.EnqueuedJobs(q.Name, 0, 100))
            .ToList();

        enqueuedJobs.Should().HaveCount(1,
            "HangfireIngestionQueue.EnqueueAsync should enqueue exactly one ingest job");
        enqueuedJobs.Single().Value.Job!.Method.Name.Should().Be(
            nameof(Connapse.Background.Jobs.IIngestionJobs.IngestAsync));

        // The continuation summary job is created in Awaiting state. MemoryStorage's
        // StatisticsDto.Awaiting is null in this version, so we look up the parent's
        // "Continuations" job property — populated by ContinueJobWith — and then fetch
        // the continuation job to confirm it targets PerDocSummaryAsync.
        string parentId = enqueuedJobs.Single().Key;
        var parentDetails = monitor.JobDetails(parentId);
        parentDetails.Should().NotBeNull();
        parentDetails.Properties.Should().ContainKey("Continuations",
            "ContinueJobWith should attach a continuation reference on the parent");

        // Parse the JSON-encoded continuations array and verify the child targets
        // PerDocSummaryAsync. The exact JSON shape ({"JobId":"<guid>","Options":<int>})
        // is Hangfire-internal but stable across 1.8.x.
        string continuationsJson = parentDetails.Properties["Continuations"];
        using var doc = System.Text.Json.JsonDocument.Parse(continuationsJson);
        var first = doc.RootElement.EnumerateArray().FirstOrDefault();
        first.ValueKind.Should().NotBe(System.Text.Json.JsonValueKind.Undefined);
        string continuationId = first.GetProperty("JobId").GetString()!;

        var childDetails = monitor.JobDetails(continuationId);
        childDetails.Should().NotBeNull();
        childDetails.Job!.Method.Name.Should().Be(
            nameof(Connapse.Background.Jobs.IIngestionJobs.PerDocSummaryAsync));
    }
}
