namespace AIKnowledge.Core.Interfaces;

/// <summary>
/// Queue for managing document ingestion jobs.
/// </summary>
public interface IIngestionQueue
{
    /// <summary>
    /// Enqueues a document for ingestion.
    /// </summary>
    /// <param name="job">The ingestion job to queue.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task EnqueueAsync(IngestionJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dequeues the next ingestion job, waiting if necessary.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The next job to process, or null if cancelled.</returns>
    Task<IngestionJob?> DequeueAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the status of a specific job or batch.
    /// </summary>
    /// <param name="jobId">The job or batch ID.</param>
    /// <returns>Status information, or null if not found.</returns>
    Task<IngestionJobStatus?> GetStatusAsync(string jobId);

    /// <summary>
    /// Gets the current queue depth.
    /// </summary>
    int QueueDepth { get; }
}

/// <summary>
/// Represents a document ingestion job.
/// </summary>
public record IngestionJob(
    string JobId,
    string DocumentId,
    string Path,
    IngestionOptions Options,
    string? BatchId = null);

/// <summary>
/// Status of an ingestion job.
/// </summary>
public record IngestionJobStatus(
    string JobId,
    IngestionJobState State,
    IngestionPhase? CurrentPhase,
    double PercentComplete,
    string? ErrorMessage,
    DateTime? StartedAt,
    DateTime? CompletedAt);

/// <summary>
/// Job processing state.
/// </summary>
public enum IngestionJobState
{
    Queued,
    Processing,
    Completed,
    Failed
}
