using System.Threading.Channels;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Pipeline;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Pipeline;

[Trait("Category", "Unit")]
public class IngestionQueueTests
{
    private static IngestionJob CreateJob(string? jobId = null, string? documentId = null) =>
        new(
            JobId: jobId ?? Guid.NewGuid().ToString(),
            DocumentId: documentId ?? Guid.NewGuid().ToString(),
            Path: "/test/file.txt",
            Options: new IngestionOptions(
                FileName: "file.txt",
                ContainerId: Guid.NewGuid().ToString()));

    [Fact]
    public async Task EnqueueAsync_SingleJob_IncreasesQueueDepth()
    {
        var queue = new IngestionQueue();
        var job = CreateJob();

        await queue.EnqueueAsync(job);

        queue.QueueDepth.Should().Be(1);
    }

    [Fact]
    public async Task EnqueueAsync_MultipleJobs_QueueDepthReflectsCount()
    {
        var queue = new IngestionQueue();

        await queue.EnqueueAsync(CreateJob());
        await queue.EnqueueAsync(CreateJob());
        await queue.EnqueueAsync(CreateJob());

        queue.QueueDepth.Should().Be(3);
    }

    [Fact]
    public async Task DequeueAsync_ReturnsEnqueuedJob()
    {
        var queue = new IngestionQueue();
        var job = CreateJob();

        await queue.EnqueueAsync(job);
        var dequeued = await queue.DequeueAsync();

        dequeued.Should().NotBeNull();
        dequeued!.JobId.Should().Be(job.JobId);
        dequeued.DocumentId.Should().Be(job.DocumentId);
    }

    [Fact]
    public async Task DequeueAsync_FIFO_ReturnsJobsInOrder()
    {
        var queue = new IngestionQueue();
        var job1 = CreateJob(jobId: "job-1");
        var job2 = CreateJob(jobId: "job-2");
        var job3 = CreateJob(jobId: "job-3");

        await queue.EnqueueAsync(job1);
        await queue.EnqueueAsync(job2);
        await queue.EnqueueAsync(job3);

        var d1 = await queue.DequeueAsync();
        var d2 = await queue.DequeueAsync();
        var d3 = await queue.DequeueAsync();

        d1!.JobId.Should().Be("job-1");
        d2!.JobId.Should().Be("job-2");
        d3!.JobId.Should().Be("job-3");
    }

    [Fact]
    public async Task DequeueAsync_DecreasesQueueDepth()
    {
        var queue = new IngestionQueue();
        await queue.EnqueueAsync(CreateJob());
        await queue.EnqueueAsync(CreateJob());

        await queue.DequeueAsync();

        queue.QueueDepth.Should().Be(1);
    }

    [Fact]
    public async Task DequeueAsync_CancelledToken_ReturnsNull()
    {
        var queue = new IngestionQueue();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await queue.DequeueAsync(cts.Token);

        result.Should().BeNull();
    }

    [Fact]
    public async Task DequeueAsync_EmptyQueue_BlocksUntilEnqueued()
    {
        var queue = new IngestionQueue();
        var job = CreateJob();
        IngestionJob? dequeued = null;

        var dequeueTask = Task.Run(async () =>
        {
            dequeued = await queue.DequeueAsync();
        });

        // Give the dequeue task time to start blocking
        await Task.Delay(50);
        dequeued.Should().BeNull();

        await queue.EnqueueAsync(job);
        await dequeueTask;

        dequeued.Should().NotBeNull();
        dequeued!.JobId.Should().Be(job.JobId);
    }

    [Fact]
    public async Task QueueDepth_EmptyQueue_ReturnsZero()
    {
        var queue = new IngestionQueue();

        queue.QueueDepth.Should().Be(0);
    }

    [Fact]
    public async Task GetStatusAsync_EnqueuedJob_ReturnsQueuedState()
    {
        var queue = new IngestionQueue();
        var job = CreateJob();

        await queue.EnqueueAsync(job);

        var status = await queue.GetStatusAsync(job.JobId);

        status.Should().NotBeNull();
        status!.State.Should().Be(IngestionJobState.Queued);
        status.JobId.Should().Be(job.JobId);
        status.DocumentId.Should().Be(job.DocumentId);
    }

    [Fact]
    public async Task GetStatusAsync_DequeuedJob_ReturnsProcessingState()
    {
        var queue = new IngestionQueue();
        var job = CreateJob();

        await queue.EnqueueAsync(job);
        await queue.DequeueAsync();

        var status = await queue.GetStatusAsync(job.JobId);

        status.Should().NotBeNull();
        status!.State.Should().Be(IngestionJobState.Processing);
        status.StartedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task GetStatusAsync_UnknownJobId_ReturnsNull()
    {
        var queue = new IngestionQueue();

        var status = await queue.GetStatusAsync("nonexistent-job");

        status.Should().BeNull();
    }

    [Fact]
    public async Task UpdateJobStatus_SetsStateAndPhase()
    {
        var queue = new IngestionQueue();
        var job = CreateJob();

        await queue.EnqueueAsync(job);

        queue.UpdateJobStatus(
            job.JobId,
            IngestionJobState.Completed,
            IngestionPhase.Complete,
            100);

        var status = await queue.GetStatusAsync(job.JobId);
        status!.State.Should().Be(IngestionJobState.Completed);
        status.CurrentPhase.Should().Be(IngestionPhase.Complete);
        status.PercentComplete.Should().Be(100);
        status.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateJobStatus_FailedState_SetsErrorMessage()
    {
        var queue = new IngestionQueue();
        var job = CreateJob();

        await queue.EnqueueAsync(job);

        queue.UpdateJobStatus(
            job.JobId,
            IngestionJobState.Failed,
            IngestionPhase.Embedding,
            50,
            "Embedding provider unavailable");

        var status = await queue.GetStatusAsync(job.JobId);
        status!.State.Should().Be(IngestionJobState.Failed);
        status.ErrorMessage.Should().Be("Embedding provider unavailable");
        status.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CancelJobForDocumentAsync_ExistingJob_ReturnsTrueAndMarksAsFailed()
    {
        var queue = new IngestionQueue();
        var job = CreateJob();

        await queue.EnqueueAsync(job);

        var result = await queue.CancelJobForDocumentAsync(job.DocumentId);

        result.Should().BeTrue();
        var status = await queue.GetStatusAsync(job.JobId);
        status!.State.Should().Be(IngestionJobState.Failed);
        status.ErrorMessage.Should().Contain("deleted");
    }

    [Fact]
    public async Task CancelJobForDocumentAsync_UnknownDocument_ReturnsFalse()
    {
        var queue = new IngestionQueue();

        var result = await queue.CancelJobForDocumentAsync("nonexistent-doc");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetAllStatuses_ReturnsAllRegisteredJobs()
    {
        var queue = new IngestionQueue();
        var job1 = CreateJob();
        var job2 = CreateJob();

        await queue.EnqueueAsync(job1);
        await queue.EnqueueAsync(job2);

        var statuses = queue.GetAllStatuses();

        statuses.Should().HaveCount(2);
        statuses.Should().ContainKey(job1.JobId);
        statuses.Should().ContainKey(job2.JobId);
    }

    [Fact]
    public async Task CleanupOldStatuses_RemovesCompletedJobsOlderThanMaxAge()
    {
        var queue = new IngestionQueue();
        var job = CreateJob();

        await queue.EnqueueAsync(job);
        queue.UpdateJobStatus(
            job.JobId,
            IngestionJobState.Completed,
            IngestionPhase.Complete,
            100);

        // Cleanup with zero max age (everything is old)
        queue.CleanupOldStatuses(TimeSpan.Zero);

        var status = await queue.GetStatusAsync(job.JobId);
        status.Should().BeNull();
    }

    [Fact]
    public async Task CleanupOldStatuses_KeepsRecentCompletedJobs()
    {
        var queue = new IngestionQueue();
        var job = CreateJob();

        await queue.EnqueueAsync(job);
        queue.UpdateJobStatus(
            job.JobId,
            IngestionJobState.Completed,
            IngestionPhase.Complete,
            100);

        // Cleanup with 1 hour max age (job is recent)
        queue.CleanupOldStatuses(TimeSpan.FromHours(1));

        var status = await queue.GetStatusAsync(job.JobId);
        status.Should().NotBeNull();
    }

    [Fact]
    public async Task RegisterJobCancellation_AllowsCancellationViaCancelJobForDocument()
    {
        var queue = new IngestionQueue();
        var job = CreateJob();

        await queue.EnqueueAsync(job);

        using var cts = new CancellationTokenSource();
        queue.RegisterJobCancellation(job.JobId, cts);

        await queue.CancelJobForDocumentAsync(job.DocumentId);

        // The CTS should be cancelled (but we can't check directly since it's disposed)
        // Verify job is marked as failed
        var status = await queue.GetStatusAsync(job.JobId);
        status!.State.Should().Be(IngestionJobState.Failed);
    }

    [Fact]
    public void UnregisterJobCancellation_DisposesTheCts()
    {
        var queue = new IngestionQueue();
        var cts = new CancellationTokenSource();

        queue.RegisterJobCancellation("job-1", cts);
        queue.UnregisterJobCancellation("job-1");

        // After unregister, the CTS should be disposed
        var act = () => cts.Token;
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void CompleteQueue_PreventsNewWrites()
    {
        var queue = new IngestionQueue();
        queue.CompleteQueue();

        var act = async () => await queue.EnqueueAsync(CreateJob());

        act.Should().ThrowAsync<ChannelClosedException>();
    }

    [Fact]
    public async Task EnqueueAsync_CancelledToken_ThrowsOperationCancelled()
    {
        var queue = new IngestionQueue();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await queue.EnqueueAsync(CreateJob(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
