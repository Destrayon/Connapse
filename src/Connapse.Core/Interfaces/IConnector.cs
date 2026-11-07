namespace Connapse.Core.Interfaces;

/// <summary>
/// Read access to a backing store. Deliberately has no write surface — external
/// connectors back sources, which mirror someone else's system and are never mutated
/// through Connapse. Managed storage adds writes via <see cref="IWritableConnector"/>.
/// </summary>
public interface IConnector
{
    ConnectorType Type { get; }

    Task<Stream> ReadFileAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default);
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Resolves a virtual/relative path to the actual job path for the ingestion queue.
    /// Cloud connectors return virtual paths.
    /// </summary>
    string ResolveJobPath(string relativePath);
}
