using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Storage.Connectors;

/// <summary>
/// IConnector implementation backed by Azure Blob Storage.
/// Uses DefaultAzureCredential (managed identity, az login, env vars).
/// No connection strings or stored keys.
/// SupportsLiveWatch = false; sync-on-demand via POST /api/containers/{id}/sync.
/// </summary>
public class AzureBlobConnector : IConnector
{
    private readonly AzureBlobConnectorConfig _config;
    private readonly BlobContainerClient _containerClient;

    public AzureBlobConnector(AzureBlobConnectorConfig config)
    {
        _config = config;
        _containerClient = CreateContainerClient(config);
    }

    public ConnectorType Type => ConnectorType.AzureBlob;
    public bool SupportsLiveWatch => false;

    public async Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
    {
        var blobName = ToBlobName(path);
        var blobClient = _containerClient.GetBlobClient(blobName);

        try
        {
            var response = await blobClient.DownloadStreamingAsync(cancellationToken: ct);
            return response.Value.Content;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            throw new FileNotFoundException(
                $"Blob not found in '{_config.StorageAccountName}/{_config.ContainerName}' at '{blobName}'.", path);
        }
    }

    public async Task WriteFileAsync(string path, Stream content, string? contentType = null, CancellationToken ct = default)
    {
        var blobName = ToBlobName(path);
        var blobClient = _containerClient.GetBlobClient(blobName);

        var options = new BlobUploadOptions();
        if (!string.IsNullOrEmpty(contentType))
            options.HttpHeaders = new BlobHttpHeaders { ContentType = contentType };

        await blobClient.UploadAsync(content, options, ct);
    }

    public async Task DeleteFileAsync(string path, CancellationToken ct = default)
    {
        var blobName = ToBlobName(path);
        var blobClient = _containerClient.GetBlobClient(blobName);
        await blobClient.DeleteIfExistsAsync(cancellationToken: ct);
    }

    public async Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
    {
        var effectivePrefix = CombinePrefix(prefix);
        var files = new List<ConnectorFile>();

        await foreach (var blob in _containerClient.GetBlobsAsync(
            traits: BlobTraits.Metadata,
            prefix: string.IsNullOrEmpty(effectivePrefix) ? null : effectivePrefix,
            cancellationToken: ct))
        {
            var virtualPath = StripConfigPrefix(blob.Name);
            files.Add(new ConnectorFile(
                Path: virtualPath,
                SizeBytes: blob.Properties.ContentLength ?? 0,
                LastModified: blob.Properties.LastModified?.UtcDateTime ?? DateTime.UtcNow,
                ContentType: blob.Properties.ContentType));
        }

        return files;
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var blobName = ToBlobName(path);
        var blobClient = _containerClient.GetBlobClient(blobName);
        var response = await blobClient.ExistsAsync(ct);
        return response.Value;
    }

    public IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(AzureBlobConnector)} does not support live watch.");

    /// <summary>
    /// Converts a virtual path (e.g. "/docs/file.md") to a blob name,
    /// prepending the configured prefix if any.
    /// </summary>
    private string ToBlobName(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (!string.IsNullOrEmpty(_config.Prefix))
        {
            var prefix = _config.Prefix.TrimEnd('/') + "/";
            return prefix + normalized;
        }
        return normalized;
    }

    /// <summary>
    /// Combines the configured prefix with an optional additional prefix for listing.
    /// </summary>
    private string CombinePrefix(string? additionalPrefix)
    {
        var configPrefix = string.IsNullOrEmpty(_config.Prefix) ? "" : _config.Prefix.TrimEnd('/') + "/";
        if (string.IsNullOrEmpty(additionalPrefix))
            return configPrefix;
        var extra = additionalPrefix.Replace('\\', '/').TrimStart('/');
        return configPrefix + extra;
    }

    /// <summary>
    /// Strips the configured prefix from a blob name to produce a virtual path with leading /.
    /// </summary>
    private string StripConfigPrefix(string blobName)
    {
        if (!string.IsNullOrEmpty(_config.Prefix))
        {
            var prefix = _config.Prefix.TrimEnd('/') + "/";
            if (blobName.StartsWith(prefix, StringComparison.Ordinal))
                blobName = blobName[prefix.Length..];
        }
        return "/" + blobName.TrimStart('/');
    }

    private static BlobContainerClient CreateContainerClient(AzureBlobConnectorConfig config)
    {
        var credentialOptions = new DefaultAzureCredentialOptions();
        if (!string.IsNullOrWhiteSpace(config.ManagedIdentityClientId))
            credentialOptions.ManagedIdentityClientId = config.ManagedIdentityClientId;

        var credential = new DefaultAzureCredential(credentialOptions);
        var serviceUri = new Uri($"https://{config.StorageAccountName}.blob.core.windows.net");
        var serviceClient = new BlobServiceClient(serviceUri, credential);
        return serviceClient.GetBlobContainerClient(config.ContainerName);
    }
}
