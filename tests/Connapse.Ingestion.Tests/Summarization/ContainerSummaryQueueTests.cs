using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Summarization;
using FluentAssertions;
using Xunit;

namespace Connapse.Ingestion.Tests.Summarization;

[Trait("Category", "Unit")]
public class ContainerSummaryQueueTests
{
    [Fact]
    public async Task EnqueueAndDequeue_RoundTripsEvent()
    {
        IContainerSummaryQueue queue = new ContainerSummaryQueue();
        Guid cid = Guid.NewGuid();
        Guid did = Guid.NewGuid();

        await queue.EnqueueAsync(new ContainerSummaryDirtyEvent(cid, did));
        ContainerSummaryDirtyEvent dequeued = await queue.DequeueAsync(CancellationToken.None);

        dequeued.ContainerId.Should().Be(cid);
        dequeued.DocumentId.Should().Be(did);
    }

    [Fact]
    public async Task DequeueAsync_BlocksUntilEnqueue()
    {
        IContainerSummaryQueue queue = new ContainerSummaryQueue();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));

        Task<ContainerSummaryDirtyEvent> dequeueTask = queue.DequeueAsync(cts.Token).AsTask();
        dequeueTask.IsCompleted.Should().BeFalse();

        await queue.EnqueueAsync(new ContainerSummaryDirtyEvent(Guid.NewGuid(), Guid.NewGuid()));
        ContainerSummaryDirtyEvent result = await dequeueTask;
        result.ContainerId.Should().NotBe(Guid.Empty);
    }
}
