using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Integration.Tests;

/// <summary>
/// Test-only connector that replicates AzureBlobConnector behaviour
/// but accepts an injected BlobContainerClient instead of using DefaultAzureCredential.
/// Path-translation logic is copied verbatim from AzureBlobConnector.
/// </summary>
internal sealed class TestableAzureBlobConnector : IConnector
{
    private readonly BlobContainerClient _containerClient;
    private readonly string? _prefix;

    public TestableAzureBlobConnector(BlobContainerClient containerClient, string? prefix = null)
    {
        _containerClient = containerClient;
        _prefix = prefix;
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
            throw new FileNotFoundException($"Blob not found at '{blobName}'.", path);
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
        => throw new NotSupportedException($"{nameof(TestableAzureBlobConnector)} does not support live watch.");

    private string ToBlobName(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (!string.IsNullOrEmpty(_prefix))
            return _prefix.TrimEnd('/') + "/" + normalized;
        return normalized;
    }

    private string CombinePrefix(string? additionalPrefix)
    {
        var configPrefix = string.IsNullOrEmpty(_prefix) ? "" : _prefix.TrimEnd('/') + "/";
        if (string.IsNullOrEmpty(additionalPrefix))
            return configPrefix;
        var extra = additionalPrefix.Replace('\\', '/').TrimStart('/');
        return configPrefix + extra;
    }

    private string StripConfigPrefix(string blobName)
    {
        if (!string.IsNullOrEmpty(_prefix))
        {
            var prefix = _prefix.TrimEnd('/') + "/";
            if (blobName.StartsWith(prefix, StringComparison.Ordinal))
                blobName = blobName[prefix.Length..];
        }
        return "/" + blobName.TrimStart('/');
    }
}
