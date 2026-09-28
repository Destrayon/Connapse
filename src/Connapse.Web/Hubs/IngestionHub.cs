using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Connapse.Web.Hubs;

/// <summary>
/// SignalR hub for real-time document status updates.
/// Requires authentication — connect with cookie (Blazor UI) or ?access_token= (JWT clients).
/// </summary>
[Authorize]
public class IngestionHub : Hub
{
    internal static string DocumentGroup(string documentId) => $"document:{documentId}";

    /// <summary>
    /// Subscribe to status changes for one document.
    /// </summary>
    public async Task SubscribeToDocument(string documentId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, DocumentGroup(documentId));
    }

    /// <summary>
    /// Unsubscribe from status changes for one document.
    /// </summary>
    public async Task UnsubscribeFromDocument(string documentId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, DocumentGroup(documentId));
    }
}
