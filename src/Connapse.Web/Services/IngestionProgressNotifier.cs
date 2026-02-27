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

    internal void Notify(IngestionProgressUpdate update) =>
        ProgressReceived?.Invoke(update);
}
