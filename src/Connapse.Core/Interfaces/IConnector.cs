namespace Connapse.Core.Interfaces;

public interface IConnector
{
    ConnectorType Type { get; }
    bool SupportsLiveWatch { get; }

    Task<Stream> ReadFileAsync(string path, CancellationToken ct = default);
    Task WriteFileAsync(string path, Stream content, string? contentType = null, CancellationToken ct = default);
    Task DeleteFileAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default);
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);

    // Throws NotSupportedException if SupportsLiveWatch is false
    IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default);
}
