using System.Net;
using AIKnowledge.Core;
using AIKnowledge.Core.Interfaces;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace AIKnowledge.Storage.FileSystem;

public class MinioFileSystem : IKnowledgeFileSystem
{
    private readonly IAmazonS3 _s3;
    private readonly MinioOptions _options;

    public MinioFileSystem(IAmazonS3 s3, IOptions<MinioOptions> options)
    {
        _s3 = s3;
        _options = options.Value;
    }

    public string RootPath => _options.BucketName;

    public string ResolvePath(string virtualPath)
    {
        var normalized = virtualPath
            .Replace('\\', '/')
            .TrimStart('/');

        // Block path traversal
        if (normalized.Contains(".."))
            throw new UnauthorizedAccessException(
                $"Path '{virtualPath}' contains invalid traversal sequence.");

        return normalized;
    }

    public Task EnsureDirectoryExistsAsync(string virtualPath, CancellationToken ct = default)
    {
        // S3/MinIO doesn't have real directories — they're just key prefixes.
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<FileSystemEntry>> ListAsync(
        string virtualPath = "/", CancellationToken ct = default)
    {
        var prefix = ResolvePath(virtualPath);
        if (prefix.Length > 0 && !prefix.EndsWith('/'))
            prefix += "/";

        var request = new ListObjectsV2Request
        {
            BucketName = _options.BucketName,
            Prefix = prefix,
            Delimiter = "/"
        };

        var entries = new List<FileSystemEntry>();
        ListObjectsV2Response response;

        do
        {
            response = await _s3.ListObjectsV2Async(request, ct);

            // "Subdirectories" (common prefixes)
            if (response.CommonPrefixes is not null)
            {
                foreach (var dirPrefix in response.CommonPrefixes)
                {
                    var name = dirPrefix.TrimEnd('/').Split('/').Last();
                    entries.Add(new FileSystemEntry(
                        Name: name,
                        VirtualPath: "/" + dirPrefix.TrimEnd('/'),
                        IsDirectory: true,
                        SizeBytes: 0,
                        LastModifiedUtc: DateTime.UtcNow));
                }
            }

            // Files
            foreach (var obj in response.S3Objects)
            {
                // Skip the prefix itself if it shows up as an object
                if (obj.Key == prefix)
                    continue;

                var name = obj.Key.Split('/').Last();
                if (string.IsNullOrEmpty(name))
                    continue;

                entries.Add(new FileSystemEntry(
                    Name: name,
                    VirtualPath: "/" + obj.Key,
                    IsDirectory: false,
                    SizeBytes: obj.Size ?? 0,
                    LastModifiedUtc: obj.LastModified?.ToUniversalTime() ?? DateTime.UtcNow));
            }

            request.ContinuationToken = response.NextContinuationToken;
        } while (response.IsTruncated == true);

        return entries;
    }

    public async Task<bool> ExistsAsync(string virtualPath, CancellationToken ct = default)
    {
        var key = ResolvePath(virtualPath);

        try
        {
            await _s3.GetObjectMetadataAsync(_options.BucketName, key, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Could be a "directory" — check if any objects exist with this prefix
            var listRequest = new ListObjectsV2Request
            {
                BucketName = _options.BucketName,
                Prefix = key.EndsWith('/') ? key : key + "/",
                MaxKeys = 1
            };

            var response = await _s3.ListObjectsV2Async(listRequest, ct);
            return response.S3Objects?.Count > 0;
        }
    }

    public async Task SaveFileAsync(
        string virtualPath, Stream content, CancellationToken ct = default)
    {
        var key = ResolvePath(virtualPath);

        var request = new PutObjectRequest
        {
            BucketName = _options.BucketName,
            Key = key,
            InputStream = content
        };

        await _s3.PutObjectAsync(request, ct);
    }

    public async Task<Stream> OpenFileAsync(string virtualPath, CancellationToken ct = default)
    {
        var key = ResolvePath(virtualPath);

        try
        {
            var response = await _s3.GetObjectAsync(_options.BucketName, key, ct);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"File not found: {virtualPath}", virtualPath);
        }
    }

    public async Task DeleteAsync(string virtualPath, CancellationToken ct = default)
    {
        var key = ResolvePath(virtualPath);

        // Try deleting as a single object first
        try
        {
            await _s3.DeleteObjectAsync(_options.BucketName, key, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Ignore — might be a "directory"
        }

        // Also delete all objects under this prefix (directory-like delete)
        var prefix = key.EndsWith('/') ? key : key + "/";
        var listRequest = new ListObjectsV2Request
        {
            BucketName = _options.BucketName,
            Prefix = prefix
        };

        ListObjectsV2Response listResponse;
        do
        {
            listResponse = await _s3.ListObjectsV2Async(listRequest, ct);

            foreach (var obj in listResponse.S3Objects)
            {
                await _s3.DeleteObjectAsync(_options.BucketName, obj.Key, ct);
            }

            listRequest.ContinuationToken = listResponse.NextContinuationToken;
        } while (listResponse.IsTruncated == true);
    }

    /// <summary>
    /// Ensures the configured bucket exists. Called during application startup.
    /// </summary>
    public async Task EnsureBucketExistsAsync(CancellationToken ct = default)
    {
        try
        {
            await _s3.GetBucketLocationAsync(_options.BucketName, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            await _s3.PutBucketAsync(_options.BucketName, ct);
        }
    }
}
