namespace Connapse.Core.Interfaces;

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
    /// Cancels any queued or in-progress ingestion job for the given document.
    /// </summary>
    /// <param name="documentId">The document ID whose job should be cancelled.</param>
    /// <returns>True if a job was found and cancelled, false otherwise.</returns>
    Task<bool> CancelJobForDocumentAsync(string documentId);

    /// <summary>
    /// Gets the current queue depth.
    /// </summary>
    int QueueDepth { get; }

    /// <summary>
    /// Updates the status of a job (phase, progress, error).
    /// </summary>
    void UpdateJobStatus(
        string jobId,
        IngestionJobState state,
        IngestionPhase? currentPhase = null,
        double percentComplete = 0,
        string? errorMessage = null);

    /// <summary>
    /// Gets all job statuses for monitoring and progress broadcasting.
    /// </summary>
    IReadOnlyDictionary<string, IngestionJobStatus> GetAllStatuses();

    /// <summary>
    /// Registers a CancellationTokenSource for a job so it can be cancelled on demand.
    /// </summary>
    void RegisterJobCancellation(string jobId, CancellationTokenSource cts);

    /// <summary>
    /// Removes the CancellationTokenSource for a completed/failed job.
    /// </summary>
    void UnregisterJobCancellation(string jobId);
}

/// <summary>
/// Represents a document ingestion job.
/// </summary>
public record IngestionJob(
    string JobId,
    string DocumentId,
    string Path,
    IngestionOptions Options,
    int Generation = 0,
    string? BatchId = null);

/// <summary>
/// Status of an ingestion job.
/// </summary>
public record IngestionJobStatus(
    string JobId,
    string DocumentId,
    string? ContainerId,
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
