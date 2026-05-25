using System.Threading.Channels;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Ingestion.Summarization;

public sealed class ContainerSummaryQueue : IContainerSummaryQueue
{
    private readonly Channel<ContainerSummaryDirtyEvent> _channel =
        Channel.CreateBounded<ContainerSummaryDirtyEvent>(
            new BoundedChannelOptions(capacity: 10_000)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

    public ValueTask EnqueueAsync(ContainerSummaryDirtyEvent evt, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(evt, ct);

    public ValueTask<ContainerSummaryDirtyEvent> DequeueAsync(CancellationToken ct) =>
        _channel.Reader.ReadAsync(ct);
}
