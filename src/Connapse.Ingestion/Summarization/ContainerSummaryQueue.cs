using System.Threading.Channels;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Ingestion.Summarization;

public sealed class ContainerSummaryQueue : IContainerSummaryQueue
{
    private readonly Channel<ContainerSummaryDirtyEvent> _channel =
        Channel.CreateUnbounded<ContainerSummaryDirtyEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public ValueTask EnqueueAsync(ContainerSummaryDirtyEvent evt, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(evt, ct);

    public ValueTask<ContainerSummaryDirtyEvent> DequeueAsync(CancellationToken ct) =>
        _channel.Reader.ReadAsync(ct);
}
