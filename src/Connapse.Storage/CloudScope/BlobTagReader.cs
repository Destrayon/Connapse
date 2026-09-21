using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Storage.CloudScope;

/// <summary>Reads a blob's index tags over <c>Azure.Storage.Blobs</c>. Thin shell; fail-closed
/// (<c>null</c>) on any error, genuine caller cancellation propagates.</summary>
public sealed class BlobTagReader(TokenCredential credential) : IBlobTagReader
{
    public async Task<IReadOnlyDictionary<string, string>?> ReadTagsAsync(Gen2Path blob, CancellationToken ct = default)
    {
        try
        {
            var service = new BlobServiceClient(
                new Uri($"https://{blob.Account}.blob.core.windows.net"), credential);
            BlobClient client = service.GetBlobContainerClient(blob.FileSystem).GetBlobClient(blob.Path);
            Response<GetBlobTagResult> tags = await client.GetTagsAsync(cancellationToken: ct);
            return MapTags(tags.Value?.Tags);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    internal static IReadOnlyDictionary<string, string> MapTags(IDictionary<string, string>? tags) =>
        tags is null ? new Dictionary<string, string>() : new Dictionary<string, string>(tags);
}
