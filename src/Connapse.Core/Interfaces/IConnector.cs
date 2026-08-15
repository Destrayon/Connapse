namespace Connapse.Core.Interfaces;

/// <summary>
/// Read access to a backing store. Deliberately has no write surface — external
/// connectors back sources, which mirror someone else's system and are never mutated
/// through Connapse. Managed storage adds writes via <see cref="IWritableConnector"/>.
/// </summary>
public interface IConnector
{
    ConnectorType Type { get; }
    bool SupportsLiveWatch { get; }

    Task<Stream> ReadFileAsync(string path, CancellationToken ct = default);
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
