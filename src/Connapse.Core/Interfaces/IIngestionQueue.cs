namespace Connapse.Core.Interfaces;

/// <summary>
/// Queue for managing document ingestion jobs.
/// </summary>
public interface IIngestionQueue
{
    /// <summary>
    /// Marks the document Queued at a new generation and enqueues a job for it. The document row
    /// must already exist.
    /// </summary>
    /// <returns>The background job's id, or null when the document does not exist.</returns>
    Task<string?> EnqueueAsync(IngestionJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the background job currently recorded for the document, if any. A job already
    /// running is not interrupted, but it can no longer complete: whatever supersedes the
    /// document bumps its generation.
    /// </summary>
    /// <returns>True if a job was found and deleted.</returns>
    Task<bool> CancelJobForDocumentAsync(string documentId);

    /// <summary>How many documents are waiting for or undergoing ingestion.</summary>
    Task<int> GetQueueDepthAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a document ingestion job.
/// </summary>
/// <param name="ResetAttempts">
/// True for a new version of the file, a reindex or a manual retry, which get a fresh attempt
/// budget. False for the sync engine re-trying a document that failed, which spends from it.
/// </param>
public record IngestionJob(
    string DocumentId,
    IngestionOptions Options,
    string? BatchId = null,
    bool ResetAttempts = true);
