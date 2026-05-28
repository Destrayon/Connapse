namespace Connapse.Core.Interfaces;

/// <summary>
/// Pushes per-document <see cref="IngestionState"/> transitions to connected UI clients
/// (Indexed, SummaryIndexed, Failed). Implemented in Connapse.Web on top of SignalR; the
/// interface lives in Core so Connapse.Background jobs can call it without a Web reference.
/// </summary>
public interface IIngestionStateBroadcaster
{
    /// <summary>
    /// Broadcasts that <paramref name="documentId"/> has transitioned to <paramref name="state"/>.
    /// Implementations are expected to be fire-and-forget — failures must not break the caller.
    /// </summary>
    Task BroadcastIngestionStateChangedAsync(
        string documentId, IngestionState state, CancellationToken ct = default);
}
