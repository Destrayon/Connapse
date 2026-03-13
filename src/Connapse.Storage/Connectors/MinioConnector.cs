using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.FileSystem;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.Connectors;

/// <summary>
/// IConnector implementation backed by MinIO (or any S3-compatible object store).
/// Uses the globally configured MinioOptions for connection, with per-container
/// prefix isolation via MinioConnectorConfig.
/// SupportsLiveWatch = false; WatchAsync throws NotSupportedException.
/// </summary>
public class MinioConnector : IConnector
{
    private readonly IAmazonS3 _s3;
    private readonly MinioOptions _options;
    private readonly MinioConnectorConfig _config;

    public MinioConnector(IAmazonS3 s3, IOptions<MinioOptions> options, MinioConnectorConfig config)
    {
        _s3 = s3;
        _options = options.Value;
        _config = config;
    }

    public ConnectorType Type => ConnectorType.MinIO;
    public bool SupportsLiveWatch => false;
    public bool SupportsWrite => true;

    public string ResolveJobPath(string relativePath) =>
        "/" + relativePath.TrimStart('/');

    public async Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
    {
        var key = ToS3Key(path);
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
        var key = ToS3Key(path);
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
        var key = ToS3Key(path);
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
        var effectivePrefix = CombinePrefix(prefix);

        var files = new List<ConnectorFile>();
        var request = new ListObjectsV2Request
        {
            BucketName = _options.BucketName,
            Prefix = effectivePrefix
        };

        ListObjectsV2Response response;
        do
        {
            response = await _s3.ListObjectsV2Async(request, ct);
            foreach (var obj in response.S3Objects ?? [])
            {
                if (obj.Key == effectivePrefix) continue;
                var virtualPath = StripConfigPrefix(obj.Key);
                files.Add(new ConnectorFile(
                    Path: virtualPath,
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
        var key = ToS3Key(path);
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

    /// <summary>
    /// Converts a virtual path to an S3 key, prepending the configured prefix if any.
    /// </summary>
    private string ToS3Key(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (!string.IsNullOrEmpty(_config.ContainerId))
        {
            var prefix = _config.ContainerId.TrimEnd('/') + "/";
            return prefix + normalized;
        }
        return normalized;
    }

    /// <summary>
    /// Combines the configured prefix with an optional additional prefix for listing.
    /// </summary>
    private string CombinePrefix(string? additionalPrefix)
    {
        var configPrefix = string.IsNullOrEmpty(_config.ContainerId) ? "" : _config.ContainerId.TrimEnd('/') + "/";
        if (string.IsNullOrEmpty(additionalPrefix))
            return configPrefix;
        var extra = additionalPrefix.Replace('\\', '/').TrimStart('/');
        return configPrefix + extra;
    }

    /// <summary>
    /// Strips the configured prefix from an S3 key to produce a virtual path with leading /.
    /// </summary>
    private string StripConfigPrefix(string key)
    {
        if (!string.IsNullOrEmpty(_config.ContainerId))
        {
            var prefix = _config.ContainerId.TrimEnd('/') + "/";
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                key = key[prefix.Length..];
        }
        return "/" + key.TrimStart('/');
    }
}
