using Azure.Core;
using Azure.Storage.Blobs;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using System.Runtime.CompilerServices;

namespace Connapse.Storage.Connectors;

/// <summary>
/// Read-only Azure Blob Storage connector. Builds a BlobServiceClient from the account +
/// Connapse's TokenCredential; the internal ctor accepts a prebuilt client for tests
/// (Azurite cannot authenticate an AAD TokenCredential). SupportsLiveWatch = false.
/// </summary>
public sealed class AzureBlobConnector : IConnector, IDisposable
{
    private readonly AzureBlobConnectorConfig _config;
    private readonly BlobContainerClient _container;

    public AzureBlobConnector(AzureBlobConnectorConfig config, TokenCredential credential)
        : this(config, new BlobServiceClient(
            new Uri(config.BlobEndpoint ?? $"https://{config.AccountName}.blob.core.windows.net"),
            credential))
    { }

    internal AzureBlobConnector(AzureBlobConnectorConfig config, BlobServiceClient client)
    {
        _config = config;
        _container = client.GetBlobContainerClient(config.ContainerName);
    }

    public ConnectorType Type => ConnectorType.AzureBlob;
    public bool SupportsLiveWatch => false;

    public string ResolveJobPath(string relativePath) => CombinePrefix(relativePath);

    public async Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
    {
        string effective = CombinePrefix(prefix ?? "");
        var files = new List<ConnectorFile>();
        await foreach (var item in _container.GetBlobsAsync(prefix: effective, cancellationToken: ct))
        {
            files.Add(new ConnectorFile(
                item.Name,
                item.Properties.ContentLength ?? 0,
                (item.Properties.LastModified ?? DateTimeOffset.MinValue).UtcDateTime,
                item.Properties.ContentType,
                ResourceUri.ForAzureBlob(_config.AccountName, _config.ContainerName, item.Name)));
        }
        return files;
    }

    public async Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
    {
        if (!IsInPrefixScope(path))
        {
            throw new UnauthorizedAccessException(
                $"Blob '{path}' is outside the source's configured prefix scope.");
        }
        var download = await _container.GetBlobClient(path).DownloadStreamingAsync(cancellationToken: ct);
        return download.Value.Content;
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        => IsInPrefixScope(path) && await _container.GetBlobClient(path).ExistsAsync(ct);

    /// <summary>
    /// Enforces the configured prefix as an exact-subtree boundary: a path is in scope only if
    /// the prefix is empty (whole container), equals the path exactly, or is followed by '/'.
    /// Prevents a sibling prefix (e.g. "team-archive/") from matching "team/" via a raw StartsWith.
    /// </summary>
    internal bool IsInPrefixScope(string path)
    {
        string normalizedPrefix = _config.Prefix?.Trim('/') ?? "";
        if (string.IsNullOrEmpty(normalizedPrefix))
        {
            return true;
        }
        return path.Equals(normalizedPrefix, StringComparison.Ordinal)
            || path.StartsWith(normalizedPrefix + "/", StringComparison.Ordinal);
    }

    public async IAsyncEnumerable<ConnectorFileEvent> WatchAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        throw new NotSupportedException("Azure Blob connector does not support live watch.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private string CombinePrefix(string tail)
    {
        string p = _config.Prefix?.Trim('/') ?? "";
        string t = tail.TrimStart('/');
        return string.IsNullOrEmpty(p) ? t : string.IsNullOrEmpty(t) ? p + "/" : $"{p}/{t}";
    }

    public void Dispose() { }
}
