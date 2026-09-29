using Connapse.Core;

namespace Connapse.Web.Services;

/// <summary>
/// In-process event bus for document status changes. Lets Blazor Server components receive
/// them without creating a server-to-server SignalR client connection (which has no auth cookies).
/// </summary>
public class IngestionProgressNotifier
{
    /// <summary>Fired after each status transition the document lifecycle commits.</summary>
    public event Action<DocumentStatusChangedEvent>? StatusChanged;

    internal void NotifyStatusChanged(DocumentStatusChangedEvent ev) =>
        StatusChanged?.Invoke(ev);
}

public record DocumentStatusChangedEvent(string DocumentId, DocumentStatus Status, SummaryStatus SummaryStatus);
