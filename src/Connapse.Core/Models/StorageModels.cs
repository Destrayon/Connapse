namespace Connapse.Core;

public enum ConnectorType { ManagedStorage = 0, Filesystem = 1, S3 = 3, Sftp = 5 }

public record ContainerSettingsOverrides
{
    public ChunkingSettings? Chunking { get; init; }
    public EmbeddingSettings? Embedding { get; init; }
    public SearchSettings? Search { get; init; }
    public UploadSettings? Upload { get; init; }
    public SummarySettings? Summary { get; init; }
}

/// <summary>One file a connector found, as the connector sees it.</summary>
/// <param name="Path">
/// Virtual path within the source: the key with the source's prefix stripped and a leading slash
/// added. This is what a document row stores, and what the connector is asked for later.
/// </param>
/// <param name="ResourceUri">
/// Where the file actually is, absolutely and outside Connapse — <c>s3://bucket/key</c> for S3.
/// Null for connectors with no meaningful external address.
/// </param>
/// <remarks>
/// <paramref name="ResourceUri"/> is reported rather than reconstructed, because reconstruction is
/// wrong in cases nothing can detect. <paramref name="Path"/> is relative to the source's prefix,
/// and a source's prefix is editable with no reconciliation of existing rows — so a source
/// re-pointed after ingestion leaves paths relative to a prefix no longer on record.
/// <para>
/// It also survives a normalisation that <paramref name="Path"/> does not. S3 permits
/// <c>docs/a.md</c>, <c>/docs/a.md</c> and <c>//docs/a.md</c> as three distinct keys; prefix
/// stripping collapses all three to one path.
/// </para>
/// </remarks>
public record ConnectorFile(
    string Path,
    long SizeBytes,
    DateTime LastModified,
    string? ContentType,
    string? ResourceUri = null);
public record ConnectorFileEvent(ConnectorFileEventType EventType, string Path, string? OldPath = null);
public enum ConnectorFileEventType { Created, Changed, Deleted, Renamed }

public record Container(
    string Id,
    string Name,
    string? Description,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int DocumentCount = 0,
    ContainerSettingsOverrides? SettingsOverrides = null,
    string? Summary = null,
    DateTime? SummaryGeneratedAt = null,
    string? SummaryDocSetHash = null);

public record CreateContainerRequest(
    string Name,
    string? Description = null);

public record Folder(string Id, string ContainerId, string Path, DateTime CreatedAt);

public record Document(
    string Id,
    string ContainerId,
    string FileName,
    string? ContentType,
    string Path,
    long SizeBytes,
    DateTime CreatedAt,
    Dictionary<string, string> Metadata,
    string? Summary = null,
    DateTime? SummaryGeneratedAt = null,
    string? SummaryContentHash = null,
    IngestionState IngestionState = IngestionState.Pending)
{
    /// <summary>
    /// Which owner the row actually has. Needed because <see cref="ContainerId"/> carries
    /// COALESCE(container_id, source_id) — it is never blank, so a source-owned document is
    /// indistinguishable from a container-owned one by that field alone, and a caller that
    /// guessed "container" would look up a container by a source's id and find nothing.
    /// <para>
    /// Null only for documents built outside the store, which have not been read from a row.
    /// </para>
    /// </summary>
    public OwnerRef? Owner { get; init; }
}

public record StoreResult(string DocumentId, int Generation);

public record ContainerStats(
    int DocumentCount,
    int ReadyCount,
    int ProcessingCount,
    int FailedCount,
    long TotalChunks,
    long TotalSizeBytes,
    DateTime? LastIndexedAt);

public record VectorSearchResult(
    string Id,
    float Score,
    Dictionary<string, string> Metadata);

public record FileSystemEntry(
    string Name,
    string VirtualPath,
    bool IsDirectory,
    long SizeBytes,
    DateTime LastModifiedUtc);

public class KnowledgeFileSystemOptions
{
    public const string SectionName = "Knowledge:FileSystem";

    /// <summary>
    /// Root directory for the managed file system. Relative paths are resolved
    /// from the application's working directory.
    /// </summary>
    public string RootPath { get; set; } = "knowledge-data";
}
