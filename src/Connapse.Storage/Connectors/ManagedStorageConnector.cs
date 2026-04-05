using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Storage.Connectors;

/// <summary>
/// IConnector implementation for platform-managed storage.
/// Delegates all operations to the deployment-specific IManagedStorageProvider.
/// </summary>
public class ManagedStorageConnector : IConnector
{
    private readonly IConnector _inner;

    public ManagedStorageConnector(IManagedStorageProvider provider, ManagedStorageConnectorConfig config)
    {
        _inner = provider.CreateConnector(config.ContainerName);
    }

    public ConnectorType Type => ConnectorType.ManagedStorage;
    public bool SupportsLiveWatch => false;
    public bool SupportsWrite => true;

    public string ResolveJobPath(string relativePath) =>
        _inner.ResolveJobPath(relativePath);

    public Task<Stream> ReadFileAsync(string path, CancellationToken ct = default) =>
        _inner.ReadFileAsync(path, ct);

    public Task WriteFileAsync(string path, Stream content, string? contentType = null, CancellationToken ct = default) =>
        _inner.WriteFileAsync(path, content, contentType, ct);

    public Task DeleteFileAsync(string path, CancellationToken ct = default) =>
        _inner.DeleteFileAsync(path, ct);

    public Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default) =>
        _inner.ListFilesAsync(prefix, ct);

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default) =>
        _inner.ExistsAsync(path, ct);

    public IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default) =>
        throw new NotSupportedException($"{nameof(ManagedStorageConnector)} does not support live watch.");
}
