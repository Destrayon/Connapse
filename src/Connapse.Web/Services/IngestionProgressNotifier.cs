using Connapse.Core;

namespace Connapse.Web.Services;

/// <summary>
/// In-process event bus for ingestion progress updates.
/// Allows Blazor Server components to receive progress notifications without
/// creating a server-to-server SignalR client connection (which has no auth cookies).
/// </summary>
public class IngestionProgressNotifier
{
    public event Action<IngestionProgressUpdate>? ProgressReceived;

    /// <summary>
    /// Fired when a document's IngestionState transitions (Pending → Indexed →
    /// SummaryIndexed → Failed). Server-to-server SignalR doesn't work from a
    /// Blazor Server circuit (no auth cookies), so background jobs route through
    /// this in-process notifier as well as the SignalR hub.
    /// </summary>
    public event Action<IngestionStateChangedEvent>? StateChanged;

    internal void Notify(IngestionProgressUpdate update) =>
        ProgressReceived?.Invoke(update);

    internal void NotifyStateChanged(string documentId, IngestionState state) =>
        StateChanged?.Invoke(new IngestionStateChangedEvent(documentId, state));
}

public record IngestionStateChangedEvent(string DocumentId, IngestionState State);
