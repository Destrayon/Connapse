namespace Connapse.Core.Interfaces;

public interface IConnector
{
    ConnectorType Type { get; }
    bool SupportsLiveWatch { get; }
    bool SupportsWrite { get; }

    Task<Stream> ReadFileAsync(string path, CancellationToken ct = default);
    Task WriteFileAsync(string path, Stream content, string? contentType = null, CancellationToken ct = default);
    Task DeleteFileAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default);
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Resolves a virtual/relative path to the actual job path for the ingestion queue.
    /// Filesystem connectors return OS-native absolute paths; cloud connectors return virtual paths.
    /// </summary>
    string ResolveJobPath(string relativePath);

    // Throws NotSupportedException if SupportsLiveWatch is false
    IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default);
}
