namespace Connapse.Core.Interfaces;

/// <summary>
/// The only writer of a document's ingestion and summary status.
/// </summary>
/// <remarks>
/// Every transition is one conditional UPDATE: it applies only if the row is still in one of the
/// states the transition starts from and still at the generation the caller was working on. A
/// false return means another worker or a newer version got there first; the caller should stop
/// without writing anything else. Each applied transition is pushed to connected UI clients.
/// <para>
/// A generation of 0 skips the generation check. Only jobs enqueued before this lifecycle
/// existed carry one.
/// </para>
/// </remarks>
public interface IDocumentLifecycle
{
    /// <summary>
    /// Any state → Queued, bumping the generation so that any job already working on an older
    /// version can no longer complete it. Returns the new generation, which the caller must put on
    /// the job it enqueues, or null when the document does not exist.
    /// </summary>
    /// <param name="resetAttempts">
    /// True for a new version of the file or a manual retry, which get a fresh attempt budget.
    /// </param>
    Task<int?> EnqueuedAsync(Guid documentId, bool resetAttempts, CancellationToken ct = default);

    /// <summary>
    /// Queued → Processing at that generation, counting the attempt. False when the job is stale
    /// or another worker already claimed the document.
    /// </summary>
    Task<bool> TryClaimAsync(Guid documentId, int generation, CancellationToken ct = default);

    /// <summary>Processing → Ready at that generation, clearing the error and the attempt count.</summary>
    Task<bool> CompleteAsync(Guid documentId, int generation, CancellationToken ct = default);

    /// <summary>Processing → Queued after a failed attempt that Hangfire will retry.</summary>
    Task<bool> RetryScheduledAsync(Guid documentId, int generation, string error, CancellationToken ct = default);

    /// <summary>Queued or Processing → a failed state, at that generation.</summary>
    Task<bool> FailAsync(
        Guid documentId, int generation, DocumentStatus failedStatus, string error, CancellationToken ct = default);

    /// <summary>
    /// Records the background job enqueued for the document at that generation. The stuck-job
    /// sweep checks it: a Queued or Processing document whose job is gone is re-enqueued.
    /// </summary>
    Task RecordJobAsync(Guid documentId, int generation, string jobId, CancellationToken ct = default);

    Task SetSummaryStatusAsync(Guid documentId, SummaryStatus status, CancellationToken ct = default);

    /// <summary>
    /// Pushes the document's current status to UI clients. For a transition committed inside a
    /// caller's own transaction — the pipeline's chunk swap — which can only be announced once
    /// that transaction has committed.
    /// </summary>
    Task NotifyAsync(Guid documentId, CancellationToken ct = default);
}

/// <summary>
/// Thrown for an ingestion failure that retrying cannot fix. The job records the document as
/// <see cref="DocumentStatus.FailedPermanent"/> and does not hand it back to Hangfire to retry.
/// </summary>
public sealed class PermanentIngestionException(string message, Exception? inner = null)
    : Exception(message, inner);
