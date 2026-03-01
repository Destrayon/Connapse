using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Storage.Connectors;

/// <summary>
/// IConnector implementation backed entirely by in-process memory.
/// Files are lost when the process exits — this is by design.
/// Chunks and embeddings are still written to PostgreSQL during the session;
/// they are cleaned up on next startup via the ephemeral container sweep.
/// SupportsLiveWatch = false; WatchAsync throws NotSupportedException.
/// </summary>
public class InMemoryConnector : IConnector
{
    private readonly ConcurrentDictionary<string, (byte[] Content, string? ContentType)> _files = new();

    public ConnectorType Type => ConnectorType.InMemory;
    public bool SupportsLiveWatch => false;

    public Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
    {
        var key = NormalizePath(path);
        if (!_files.TryGetValue(key, out var entry))
            throw new FileNotFoundException($"In-memory file not found: '{key}'.", key);

        return Task.FromResult<Stream>(new MemoryStream(entry.Content, writable: false));
    }

    public async Task WriteFileAsync(string path, Stream content, string? contentType = null, CancellationToken ct = default)
    {
        var key = NormalizePath(path);
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        _files[key] = (ms.ToArray(), contentType);
    }

    public Task DeleteFileAsync(string path, CancellationToken ct = default)
    {
        _files.TryRemove(NormalizePath(path), out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
    {
        var normalizedPrefix = string.IsNullOrEmpty(prefix) ? "" : NormalizePath(prefix);

        var files = _files.Keys
            .Where(k => string.IsNullOrEmpty(normalizedPrefix) || k.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(k =>
            {
                _files.TryGetValue(k, out var entry);
                return new ConnectorFile(k, entry.Content?.Length ?? 0, DateTime.UtcNow, entry.ContentType);
            })
            .ToList() as IReadOnlyList<ConnectorFile>;

        return Task.FromResult(files);
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        => Task.FromResult(_files.ContainsKey(NormalizePath(path)));

    public IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(InMemoryConnector)} does not support live watch.");

    /// <summary>Clears all in-memory file content. Called during startup cleanup of ephemeral containers.</summary>
    public void Clear() => _files.Clear();

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');
}
