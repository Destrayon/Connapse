using AIKnowledge.Core;
using AIKnowledge.Core.Interfaces;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace AIKnowledge.Ingestion.Pipeline;

/// <summary>
/// Thread-safe queue for managing document ingestion jobs using System.Threading.Channels.
/// </summary>
public class IngestionQueue : IIngestionQueue
{
    private readonly Channel<IngestionJob> _channel;
    private readonly ConcurrentDictionary<string, IngestionJobStatus> _jobStatuses;

    public IngestionQueue(int capacity = 1000)
    {
        // Create bounded channel with specified capacity
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        };

        _channel = Channel.CreateBounded<IngestionJob>(options);
        _jobStatuses = new ConcurrentDictionary<string, IngestionJobStatus>();
    }

    public int QueueDepth => _channel.Reader.Count;

    public async Task EnqueueAsync(IngestionJob job, CancellationToken cancellationToken = default)
    {
        // Register job status
        _jobStatuses[job.JobId] = new IngestionJobStatus(
            JobId: job.JobId,
            State: IngestionJobState.Queued,
            CurrentPhase: null,
            PercentComplete: 0,
            ErrorMessage: null,
            StartedAt: null,
            CompletedAt: null);

        // Write to channel
        await _channel.Writer.WriteAsync(job, cancellationToken);
    }

    public async Task<IngestionJob?> DequeueAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var job = await _channel.Reader.ReadAsync(cancellationToken);

            // Update status to Processing
            if (_jobStatuses.TryGetValue(job.JobId, out var status))
            {
                _jobStatuses[job.JobId] = status with
                {
                    State = IngestionJobState.Processing,
                    StartedAt = DateTime.UtcNow
                };
            }

            return job;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public Task<IngestionJobStatus?> GetStatusAsync(string jobId)
    {
        _jobStatuses.TryGetValue(jobId, out var status);
        return Task.FromResult(status);
    }

    /// <summary>
    /// Updates the status of a job.
    /// </summary>
    public void UpdateJobStatus(
        string jobId,
        IngestionJobState state,
        IngestionPhase? currentPhase = null,
        double percentComplete = 0,
        string? errorMessage = null)
    {
        if (_jobStatuses.TryGetValue(jobId, out var currentStatus))
        {
            _jobStatuses[jobId] = currentStatus with
            {
                State = state,
                CurrentPhase = currentPhase,
                PercentComplete = percentComplete,
                ErrorMessage = errorMessage,
                CompletedAt = state is IngestionJobState.Completed or IngestionJobState.Failed
                    ? DateTime.UtcNow
                    : currentStatus.CompletedAt
            };
        }
    }

    /// <summary>
    /// Marks the queue as complete (no more writes).
    /// </summary>
    public void CompleteQueue()
    {
        _channel.Writer.Complete();
    }

    /// <summary>
    /// Gets all job statuses for monitoring.
    /// </summary>
    public IReadOnlyDictionary<string, IngestionJobStatus> GetAllStatuses()
    {
        return _jobStatuses;
    }

    /// <summary>
    /// Clears completed job statuses older than the specified age.
    /// </summary>
    public void CleanupOldStatuses(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;

        var oldJobs = _jobStatuses
            .Where(kvp => kvp.Value.CompletedAt.HasValue && kvp.Value.CompletedAt.Value < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var jobId in oldJobs)
        {
            _jobStatuses.TryRemove(jobId, out _);
        }
    }
}
