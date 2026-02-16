namespace AIKnowledge.Core;

public record Document(
    string Id,
    string FileName,
    string? ContentType,
    string? CollectionId,
    long SizeBytes,
    DateTime CreatedAt,
    Dictionary<string, string> Metadata);

public record VectorSearchResult(
    string Id,
    float Score,
    Dictionary<string, string> Metadata);

public record WebSearchResult(
    List<WebSearchHit> Hits,
    int TotalResults);

public record WebSearchHit(
    string Title,
    string Url,
    string Snippet,
    float? Score);

public record WebSearchOptions(
    int MaxResults = 10,
    string? Region = null,
    string? Language = null);

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
