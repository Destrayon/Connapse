namespace Connapse.Core.Interfaces;

public interface IContainerSummaryQueue
{
    ValueTask EnqueueAsync(ContainerSummaryDirtyEvent evt, CancellationToken ct = default);
    ValueTask<ContainerSummaryDirtyEvent> DequeueAsync(CancellationToken ct);
}
