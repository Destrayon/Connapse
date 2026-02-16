using Microsoft.AspNetCore.SignalR;

namespace AIKnowledge.Web.Hubs;

/// <summary>
/// SignalR hub for real-time ingestion progress updates.
/// </summary>
public class IngestionHub : Hub
{
    /// <summary>
    /// Subscribe to progress updates for a specific job or batch.
    /// </summary>
    public async Task SubscribeToJob(string jobId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, jobId);
    }

    /// <summary>
    /// Unsubscribe from progress updates for a specific job or batch.
    /// </summary>
    public async Task UnsubscribeFromJob(string jobId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, jobId);
    }
}
