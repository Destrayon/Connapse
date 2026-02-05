using AIKnowledge.Core.Interfaces;
using AIKnowledge.Ingestion.Pipeline;
using AIKnowledge.Web.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AIKnowledge.Web.Services;

/// <summary>
/// Background service that monitors ingestion job progress and broadcasts
/// updates via SignalR to connected clients.
/// </summary>
public class IngestionProgressBroadcaster : BackgroundService
{
    private readonly IIngestionQueue _queue;
    private readonly IHubContext<IngestionHub> _hubContext;
    private readonly ILogger<IngestionProgressBroadcaster> _logger;
    private readonly Dictionary<string, DateTime> _lastBroadcast = new();

    public IngestionProgressBroadcaster(
        IIngestionQueue queue,
        IHubContext<IngestionHub> hubContext,
        ILogger<IngestionProgressBroadcaster> logger)
    {
        _queue = queue;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IngestionProgressBroadcaster started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Get all current job statuses
                if (_queue is IngestionQueue queue)
                {
                    var allStatuses = queue.GetAllStatuses();

                    foreach (var (jobId, status) in allStatuses)
                    {
                        // Only broadcast if status has changed or hasn't been broadcast recently
                        if (ShouldBroadcast(jobId, status))
                        {
                            // Broadcast to job-specific group
                            await _hubContext.Clients.Group(jobId).SendAsync(
                                "IngestionProgress",
                                new
                                {
                                    jobId,
                                    state = status.State.ToString(),
                                    currentPhase = status.CurrentPhase?.ToString(),
                                    percentComplete = status.PercentComplete,
                                    errorMessage = status.ErrorMessage,
                                    startedAt = status.StartedAt,
                                    completedAt = status.CompletedAt
                                },
                                stoppingToken);

                            _lastBroadcast[jobId] = DateTime.UtcNow;
                        }
                    }

                    // Clean up old broadcast tracking (jobs completed > 5 minutes ago)
                    var cutoff = DateTime.UtcNow.AddMinutes(-5);
                    var oldJobs = _lastBroadcast
                        .Where(kvp => !allStatuses.ContainsKey(kvp.Key) || kvp.Value < cutoff)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var jobId in oldJobs)
                    {
                        _lastBroadcast.Remove(jobId);
                    }
                }

                // Wait 500ms before next poll
                await Task.Delay(500, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting ingestion progress");
                await Task.Delay(1000, stoppingToken); // Longer delay on error
            }
        }

        _logger.LogInformation("IngestionProgressBroadcaster stopped");
    }

    private bool ShouldBroadcast(string jobId, IngestionJobStatus status)
    {
        // Always broadcast if never broadcast before
        if (!_lastBroadcast.ContainsKey(jobId))
            return true;

        // Always broadcast completed/failed jobs once
        if (status.State is IngestionJobState.Completed or IngestionJobState.Failed)
        {
            var lastTime = _lastBroadcast[jobId];
            // Only broadcast completion once
            return status.CompletedAt > lastTime;
        }

        // Throttle active jobs (max 2 updates per second)
        var elapsed = DateTime.UtcNow - _lastBroadcast[jobId];
        return elapsed.TotalMilliseconds >= 500;
    }
}
