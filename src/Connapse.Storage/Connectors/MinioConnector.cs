using System.Net;
using System.Runtime.CompilerServices;
using Amazon.S3;
using Amazon.S3.Model;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.FileSystem;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.Connectors;

/// <summary>
/// IConnector implementation backed by MinIO (or any S3-compatible object store).
/// Uses the globally configured StorageSettings — no per-container MinIO server.
/// SupportsLiveWatch = false; WatchAsync throws NotSupportedException.
/// </summary>
public class MinioConnector : IConnector
{
    private readonly IAmazonS3 _s3;
    private readonly MinioOptions _options;

    public MinioConnector(IAmazonS3 s3, IOptions<MinioOptions> options)
    {
        _s3 = s3;
        _options = options.Value;
    }

    public ConnectorType Type => ConnectorType.MinIO;
    public bool SupportsLiveWatch => false;

    public async Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
    {
        var key = NormalizePath(path);
        try
        {
            var response = await _s3.GetObjectAsync(_options.BucketName, key, ct);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"File not found in MinIO at '{key}'.", path);
        }
    }

    public async Task WriteFileAsync(string path, Stream content, string? contentType = null, CancellationToken ct = default)
    {
        var key = NormalizePath(path);
        var request = new PutObjectRequest
        {
            BucketName = _options.BucketName,
            Key = key,
            InputStream = content,
            ContentType = contentType
        };
        await _s3.PutObjectAsync(request, ct);
    }

    public async Task DeleteFileAsync(string path, CancellationToken ct = default)
    {
        var key = NormalizePath(path);
        try
        {
            await _s3.DeleteObjectAsync(_options.BucketName, key, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already deleted — treat as success
        }
    }

    public async Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
    {
        var s3Prefix = string.IsNullOrEmpty(prefix) ? "" : NormalizePath(prefix);

        var files = new List<ConnectorFile>();
        var request = new ListObjectsV2Request
        {
            BucketName = _options.BucketName,
            Prefix = s3Prefix
        };

        ListObjectsV2Response response;
        do
        {
            response = await _s3.ListObjectsV2Async(request, ct);
            foreach (var obj in response.S3Objects ?? [])
            {
                if (obj.Key == s3Prefix) continue; // skip the prefix object itself
                files.Add(new ConnectorFile(
                    Path: "/" + obj.Key.TrimStart('/'),
                    SizeBytes: obj.Size ?? 0,
                    LastModified: obj.LastModified?.ToUniversalTime() ?? DateTime.UtcNow,
                    ContentType: null));
            }
            request.ContinuationToken = response.NextContinuationToken;
        } while (response.IsTruncated == true);

        return files;
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var key = NormalizePath(path);
        try
        {
            await _s3.GetObjectMetadataAsync(_options.BucketName, key, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(MinioConnector)} does not support live watch.");

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');
}
