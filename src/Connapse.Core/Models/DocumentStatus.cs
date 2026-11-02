namespace Connapse.Core;

/// <summary>
/// Where a document is in ingestion. The only status a document has: searchability is
/// <see cref="Ready"/>.
/// </summary>
/// <remarks>Written only by <see cref="Interfaces.IDocumentLifecycle"/>.</remarks>
public enum DocumentStatus
{
    /// <summary>The row exists and an ingestion job is scheduled (or a retry is).</summary>
    Queued = 0,

    /// <summary>A worker has claimed the document.</summary>
    Processing = 1,

    /// <summary>Chunks and vectors are written; the document is searchable.</summary>
    Ready = 2,

    /// <summary>
    /// Failed for a reason that may pass — a provider down, a network timeout. Source sync and a
    /// manual retry may requeue it while its attempt budget lasts.
    /// </summary>
    FailedRetryable = 3,

    /// <summary>
    /// Failed for a reason retrying cannot fix — an unparseable file, no extractable text. Retried
    /// only when the file itself changes, or by hand.
    /// </summary>
    FailedPermanent = 4,
}

public static class DocumentStatusExtensions
{
    /// <summary>
    /// The string REST and MCP have always returned. Kept stable because clients outside this
    /// repository — connapse-cli — compare against it.
    /// </summary>
    public static string ToApiString(this DocumentStatus status) => status switch
    {
        DocumentStatus.Queued => "Pending",
        DocumentStatus.Processing => "Processing",
        DocumentStatus.Ready => "Ready",
        _ => "Failed",
    };

    public static bool IsFailed(this DocumentStatus status) =>
        status is DocumentStatus.FailedRetryable or DocumentStatus.FailedPermanent;

    public static bool IsInFlight(this DocumentStatus status) =>
        status is DocumentStatus.Queued or DocumentStatus.Processing;
}
