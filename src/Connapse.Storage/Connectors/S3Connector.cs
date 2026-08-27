using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Storage.Connectors;

/// <summary>
/// IConnector implementation backed by AWS S3.
/// Creates its own AmazonS3Client per instance using DefaultAWSCredentials (IAM roles, env vars, instance profile).
/// If RoleArn is configured, uses STS AssumeRole for cross-account access.
/// SupportsLiveWatch = false; sync-on-demand via POST /api/containers/{id}/sync.
/// </summary>
public class S3Connector : IConnector, IDisposable
{
    private readonly S3ConnectorConfig _config;
    private readonly IAmazonS3 _s3;
    private bool _disposed;

    /// <param name="baseCredentials">
    /// What Connapse acts as before any role is assumed. Null falls back to the SDK's own chain,
    /// which is what the tests and any caller outside the container do.
    /// </param>
    public S3Connector(S3ConnectorConfig config, AWSCredentials? baseCredentials = null)
    {
        _config = config;
        _s3 = CreateS3Client(config, baseCredentials);
    }

    public ConnectorType Type => ConnectorType.S3;
    public bool SupportsLiveWatch => false;

    /// <summary>
    /// The resolved configuration, exposed so tests can assert on what ConnectorFactory
    /// recombined from a connection and a source.
    /// </summary>
    internal S3ConnectorConfig Config => _config;

    public string ResolveJobPath(string relativePath) =>
        "/" + relativePath.TrimStart('/');

    public async Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
    {
        var key = ToS3Key(path);
        try
        {
            var response = await _s3.GetObjectAsync(_config.BucketName, key, ct);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"File not found in S3 bucket '{_config.BucketName}' at '{key}'.", path);
        }
    }

    public async Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
    {
        var effectivePrefix = CombinePrefix(prefix);

        var files = new List<ConnectorFile>();
        var request = new ListObjectsV2Request
        {
            BucketName = _config.BucketName,
            Prefix = effectivePrefix
        };

        ListObjectsV2Response response;
        do
        {
            response = await _s3.ListObjectsV2Async(request, ct);
            foreach (var obj in response.S3Objects ?? [])
            {
                if (obj.Key == effectivePrefix) continue;
                // Return virtual path: strip config prefix, ensure leading /
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
            await _s3.GetObjectMetadataAsync(_config.BucketName, key, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(S3Connector)} does not support live watch.");

    /// <summary>
    /// Converts a virtual path (e.g. "/docs/file.md") to an S3 key,
    /// prepending the configured prefix if any.
    /// </summary>
    private string ToS3Key(string path)
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
    /// Strips the configured prefix from an S3 key to produce a virtual path with leading /.
    /// </summary>
    private string StripConfigPrefix(string key)
    {
        if (!string.IsNullOrEmpty(_config.Prefix))
        {
            var prefix = _config.Prefix.TrimEnd('/') + "/";
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                key = key[prefix.Length..];
        }
        return "/" + key.TrimStart('/');
    }

    /// <summary>
    /// Builds an STS role session name, clamped to the 64-character AWS limit.
    /// </summary>
    internal static string BuildRoleSessionName()
    {
        string name = $"connapse-{Guid.NewGuid():N}";
        return name.Length <= 64 ? name : name[..64];
    }

    /// <remarks>
    /// The credential passed in is the identity an administrator configured, if there is one. It
    /// has to reach the STS call as well as the S3 one: assuming a role from the ambient chain
    /// while the rest of Connapse runs as a configured identity would silently sync as a different
    /// principal than every other part of the product reports.
    /// </remarks>
    private static IAmazonS3 CreateS3Client(S3ConnectorConfig config, AWSCredentials? baseCredentials)
    {
        var region = RegionEndpoint.GetBySystemName(config.Region);

        if (!string.IsNullOrWhiteSpace(config.RoleArn))
        {
            // Disposed. ConnectorFactory builds a connector every sync cycle, so without this
            // each cross-account source leaks a client and its HTTP handler per cycle.
            using var stsClient = baseCredentials is null
                ? new AmazonSecurityTokenServiceClient(region)
                : new AmazonSecurityTokenServiceClient(baseCredentials, region);
            var assumeResponse = stsClient.AssumeRoleAsync(new AssumeRoleRequest
            {
                RoleArn = config.RoleArn,
                // AWS caps RoleSessionName at 64 characters, so this truncates. It must clamp
                // to the actual length: the literal is 41 characters ("connapse-" plus a
                // 32-char GUID), and a bare [..64] throws ArgumentOutOfRangeException — which
                // meant assuming a role never worked at all.
                RoleSessionName = BuildRoleSessionName(),
                DurationSeconds = 3600
            }).GetAwaiter().GetResult();

            var sessionCredentials = new SessionAWSCredentials(
                assumeResponse.Credentials.AccessKeyId,
                assumeResponse.Credentials.SecretAccessKey,
                assumeResponse.Credentials.SessionToken);

            return new AmazonS3Client(sessionCredentials, region);
        }

        // The configured identity, or the SDK's own chain when there is none.
        return baseCredentials is null
            ? new AmazonS3Client(region)
            : new AmazonS3Client(baseCredentials, region);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _s3.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
