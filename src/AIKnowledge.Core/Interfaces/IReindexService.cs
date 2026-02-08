namespace AIKnowledge.Core.Interfaces;

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
    Failed
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
    Error
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
