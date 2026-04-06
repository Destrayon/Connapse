namespace Connapse.Core;

public enum ConnectorType { ManagedStorage = 0, Filesystem = 1, S3 = 3, AzureBlob = 4 }

public record ContainerSettingsOverrides
{
    public ChunkingSettings? Chunking { get; init; }
    public EmbeddingSettings? Embedding { get; init; }
    public SearchSettings? Search { get; init; }
    public UploadSettings? Upload { get; init; }
}

public record ConnectorFile(string Path, long SizeBytes, DateTime LastModified, string? ContentType);
public record ConnectorFileEvent(ConnectorFileEventType EventType, string Path, string? OldPath = null);
public enum ConnectorFileEventType { Created, Changed, Deleted, Renamed }

public record Container(
    string Id,
    string Name,
    string? Description,
    ConnectorType ConnectorType,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int DocumentCount = 0,
    ContainerSettingsOverrides? SettingsOverrides = null,
    string? ConnectorConfig = null);

public record CreateContainerRequest(
    string Name,
    string? Description = null,
    ConnectorType ConnectorType = ConnectorType.ManagedStorage,
    string? ConnectorConfig = null);

public record Folder(string Id, string ContainerId, string Path, DateTime CreatedAt);

public record Document(
    string Id,
    string ContainerId,
    string FileName,
    string? ContentType,
    string Path,
    long SizeBytes,
    DateTime CreatedAt,
    Dictionary<string, string> Metadata);

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
