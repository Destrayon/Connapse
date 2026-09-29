using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Connapse.Web.Services;

/// <summary>
/// Pushes each document status transition to Blazor components (in-process) and to SignalR
/// clients subscribed to that document. Called by the document lifecycle as transitions commit,
/// so nothing is polled.
/// </summary>
public class IngestionProgressBroadcaster(
    IHubContext<IngestionHub> hubContext,
    IngestionProgressNotifier notifier) : IIngestionStateBroadcaster
{
    public async Task BroadcastStatusChangedAsync(
        string documentId, DocumentStatus status, SummaryStatus summaryStatus, CancellationToken ct = default)
    {
        // In-process first — Blazor Server components subscribe here because a server-to-server
        // SignalR client has no auth cookies.
        notifier.NotifyStatusChanged(new DocumentStatusChangedEvent(documentId, status, summaryStatus));

        await hubContext.Clients.Group(IngestionHub.DocumentGroup(documentId)).SendAsync(
            "DocumentStatusChanged",
            new
            {
                DocumentId = documentId,
                Status = status.ToApiString(),
                FailureKind = status switch
                {
                    DocumentStatus.FailedPermanent => "Permanent",
                    DocumentStatus.FailedRetryable => "Retryable",
                    _ => null,
                },
                SummaryStatus = summaryStatus.ToString(),
            },
            ct);
    }
}
