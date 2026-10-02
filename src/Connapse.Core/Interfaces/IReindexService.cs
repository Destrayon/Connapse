namespace Connapse.Core.Interfaces;

/// <summary>
/// Service for reindexing documents in the knowledge base.
/// Compares content hashes and settings to determine which documents need reprocessing.
/// </summary>
public interface IReindexService
{
    /// <summary>
    /// Reindexes documents based on the provided options.
    /// Compares content hashes against stored values, only reprocessing changed documents
    /// unless force mode is enabled.
    /// </summary>
    /// <param name="options">Reindex configuration options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Summary of the reindex operation.</returns>
    Task<ReindexResult> ReindexAsync(ReindexOptions options, CancellationToken ct = default);

    /// <summary>
    /// Checks if a specific document needs reindexing based on content hash and settings.
    /// </summary>
    /// <param name="documentId">The document ID to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Information about whether reindex is needed and why.</returns>
    Task<ReindexCheck> CheckDocumentAsync(string documentId, CancellationToken ct = default);

    /// <summary>
    /// Re-enqueues documents with the settings they were last enqueued with.
    /// </summary>
    /// <param name="resetAttempts">
    /// False for a job that was lost, whose attempts carry on; true for a manual retry, which
    /// gets a fresh budget.
    /// </param>
    /// <returns>How many were enqueued.</returns>
    Task<int> RequeueAsync(IReadOnlyCollection<Guid> documentIds, bool resetAttempts = false, CancellationToken ct = default);

    /// <summary>
    /// Re-enqueues every failed document of one container or source, with a fresh attempt budget —
    /// the manual "retry" for when whatever made them fail has been fixed.
    /// </summary>
    /// <returns>How many were enqueued.</returns>
    Task<int> RetryFailedAsync(Guid ownerId, CancellationToken ct = default);
}

/// <summary>
/// Options for reindex operation.
/// </summary>
public record ReindexOptions
{
    /// <summary>
    /// Filter to specific container. Null = all containers.
    /// </summary>
    public string? ContainerId { get; init; }

    /// <summary>
    /// Specific document IDs to reindex. Null = all documents matching ContainerId filter.
    /// </summary>
    public IReadOnlyList<string>? DocumentIds { get; init; }

    /// <summary>
    /// Force reindex even if content hash hasn't changed.
    /// Useful when chunking/embedding settings have changed.
    /// </summary>
    public bool Force { get; init; } = false;

    /// <summary>
    /// Automatically detect and reindex documents where chunking or embedding
    /// settings have changed since last indexing.
    /// </summary>
    public bool DetectSettingsChanges { get; init; } = true;

    /// <summary>
    /// Chunking strategy to use for reindex. Null = use current settings.
    /// </summary>
    public ChunkingStrategy? Strategy { get; init; }

    /// <summary>
    /// Evaluate every document and report what would be re-queued, by reason, without
    /// enqueuing anything. A parser-version bump or a settings change can re-parse and re-embed
    /// a whole corpus; this shows the size of that before it starts.
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Enqueue at most this many documents; the rest that need it are reported as deferred and
    /// picked up by the next run. Null = no cap. Lets a large re-parse go out in batches.
    /// </summary>
    public int? MaxDocuments { get; init; }
}

/// <summary>
/// Result of a reindex operation.
/// </summary>
public record ReindexResult
{
    /// <summary>
    /// Batch ID for tracking the reindex operation.
    /// </summary>
    public required string BatchId { get; init; }

    /// <summary>
    /// Total number of documents evaluated.
    /// </summary>
    public int TotalDocuments { get; init; }

    /// <summary>
    /// Number of documents enqueued for reprocessing.
    /// </summary>
    public int EnqueuedCount { get; init; }

    /// <summary>
    /// Number of documents skipped (unchanged).
    /// </summary>
    public int SkippedCount { get; init; }

    /// <summary>
    /// Number of documents that failed during evaluation.
    /// </summary>
    public int FailedCount { get; init; }

    /// <summary>Dry run only: documents that would have been enqueued.</summary>
    public int PlannedCount { get; init; }

    /// <summary>Documents that need reindexing but were held back by MaxDocuments.</summary>
    public int DeferredCount { get; init; }

    /// <summary>True when nothing was enqueued because the run was a dry run.</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Reasons why documents were enqueued.
    /// </summary>
    public IReadOnlyDictionary<ReindexReason, int> ReasonCounts { get; init; } =
        new Dictionary<ReindexReason, int>();

    /// <summary>
    /// Details for each document processed.
    /// </summary>
    public IReadOnlyList<ReindexDocumentResult> Documents { get; init; } = [];
}

/// <summary>
/// Result for a single document in a reindex operation.
/// </summary>
public record ReindexDocumentResult(
    string DocumentId,
    string FileName,
    ReindexAction Action,
    ReindexReason Reason,
    string? JobId = null,
    string? ErrorMessage = null);

/// <summary>
/// Action taken for a document during reindex.
/// </summary>
public enum ReindexAction
{
    /// <summary>Document was enqueued for reprocessing.</summary>
    Enqueued,
    /// <summary>Document was skipped (no changes detected).</summary>
    Skipped,
    /// <summary>Document evaluation failed.</summary>
    Failed,
    /// <summary>Dry run: the document would have been enqueued.</summary>
    Planned,
    /// <summary>The document needs reindexing but the run's MaxDocuments cap was reached.</summary>
    Deferred
}

/// <summary>
/// Reason for reindex action.
/// </summary>
public enum ReindexReason
{
    /// <summary>No reindex needed - content unchanged.</summary>
    Unchanged,
    /// <summary>File content hash changed.</summary>
    ContentChanged,
    /// <summary>Chunking settings changed since last index.</summary>
    ChunkingSettingsChanged,
    /// <summary>Embedding model/settings changed since last index.</summary>
    EmbeddingSettingsChanged,
    /// <summary>Force reindex was requested.</summary>
    Forced,
    /// <summary>File not found in storage.</summary>
    FileNotFound,
    /// <summary>Document has never been indexed.</summary>
    NeverIndexed,
    /// <summary>Error occurred during evaluation.</summary>
    Error,
    /// <summary>A different parser, or a newer version of it, would now read the file.</summary>
    ParserChanged,
    /// <summary>Pages failed to extract last time; the chunks are partial or an older parse.</summary>
    ExtractionIncomplete,
    /// <summary>Already queued or being ingested; enqueueing it again would supersede that job.</summary>
    AlreadyQueued
}

/// <summary>
/// Result of checking if a document needs reindexing.
/// </summary>
public record ReindexCheck(
    string DocumentId,
    bool NeedsReindex,
    ReindexReason Reason,
    string? CurrentHash = null,
    string? StoredHash = null,
    string? CurrentChunkingStrategy = null,
    string? StoredChunkingStrategy = null,
    string? CurrentEmbeddingModel = null,
    string? StoredEmbeddingModel = null);
