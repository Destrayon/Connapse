namespace Connapse.Core.Interfaces;

/// <summary>
/// Pushes per-document status transitions to connected UI clients. Implemented in Connapse.Web
/// on top of SignalR; the interface lives in Core so the lifecycle can call it without a Web
/// reference.
/// </summary>
public interface IIngestionStateBroadcaster
{
    /// <summary>
    /// Broadcasts a document's status after a transition. Implementations are expected to be
    /// fire-and-forget — failures must not break the caller.
    /// </summary>
    Task BroadcastStatusChangedAsync(
        string documentId, DocumentStatus status, SummaryStatus summaryStatus, CancellationToken ct = default);
}
