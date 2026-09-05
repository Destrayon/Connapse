using System.Diagnostics;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using Connapse.Storage.Connectors;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>
/// Tests that Connapse can read a bucket, as the identity it will sync with.
/// </summary>
/// <remarks>
/// Through <see cref="ConnapseAwsCredentials"/>, which is the whole point of the test: it has to
/// run as the thing that will do the work, or a pass means nothing.
/// <para>
/// A <c>RoleArn</c> is still assumed via STS when one is given, but from these credentials rather
/// than from whatever the environment happens to offer.
/// </para>
/// </remarks>
public class S3ConnectionTester : IConnectionTester
{
    private readonly ILogger<S3ConnectionTester> _logger;
    private readonly ConnapseAwsCredentials _credentials;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public S3ConnectionTester(ConnapseAwsCredentials credentials, ILogger<S3ConnectionTester> logger)
    {
        _credentials = credentials;
        _logger = logger;
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(
        object settings,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        timeout ??= TimeSpan.FromSeconds(15);

        // Named here so a failure message can say which bucket and region it was about.
        string bucket = "?";
        string regionName = "?";

        try
        {
            var config = ExtractConfig(settings);
            bucket = config.BucketName;
            regionName = config.Region;

            if (string.IsNullOrWhiteSpace(config.BucketName))
            {
                return ConnectionTestResult.CreateFailure(
                    "Enter a bucket to test against.",
                    new Dictionary<string, object> { ["error"] = "Missing BucketName in config" });
            }

            _logger.LogDebug("Testing S3 connection to bucket {Bucket} in region {Region}",
                config.BucketName, config.Region);

            var region = RegionEndpoint.GetBySystemName(config.Region);

            IAmazonS3 s3Client;
            if (!string.IsNullOrWhiteSpace(config.RoleArn))
            {
                using var stsClient = new AmazonSecurityTokenServiceClient(_credentials, region);
                var assumeResponse = await stsClient.AssumeRoleAsync(new AssumeRoleRequest
                {
                    RoleArn = config.RoleArn,
                    RoleSessionName = "connapse-test",
                    DurationSeconds = 900
                }, ct);

                var credentials = new SessionAWSCredentials(
                    assumeResponse.Credentials.AccessKeyId,
                    assumeResponse.Credentials.SecretAccessKey,
                    assumeResponse.Credentials.SessionToken);

                s3Client = new AmazonS3Client(credentials, new AmazonS3Config
                {
                    RegionEndpoint = region,
                    Timeout = timeout
                });
            }
            else
            {
                s3Client = new AmazonS3Client(_credentials, new AmazonS3Config
                {
                    RegionEndpoint = region,
                    Timeout = timeout
                });
            }

            using var _ = s3Client;

            // Test: list up to 5 objects to verify read access
            var listResponse = await s3Client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = config.BucketName,
                Prefix = config.Prefix,
                MaxKeys = 5
            }, ct);

            stopwatch.Stop();

            var objectCount = listResponse.S3Objects?.Count ?? 0;
            var hasMore = listResponse.IsTruncated == true;
            // Says which layers passed: reached AWS, authenticated, authorised, and read the bucket.
            string where = string.IsNullOrEmpty(config.Prefix) ? "" : $" under prefix '{config.Prefix}'";
            string what = objectCount > 0
                ? $"{objectCount}{(hasMore ? "+" : "")} object{(objectCount != 1 ? "s" : "")} found{where}"
                : $"it is empty{where}";
            var message = $"Reached AWS, authenticated, and listed bucket '{config.BucketName}' in {config.Region}: {what}.";

            return ConnectionTestResult.CreateSuccess(
                message,
                new Dictionary<string, object>
                {
                    ["bucketName"] = config.BucketName,
                    ["region"] = config.Region,
                    ["prefix"] = config.Prefix ?? "(none)",
                    ["objectsFound"] = objectCount,
                    ["hasMore"] = hasMore,
                    ["usedAssumeRole"] = !string.IsNullOrWhiteSpace(config.RoleArn)
                },
                stopwatch.Elapsed);
        }
        catch (AmazonS3Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "S3 connection test failed with S3 error");

            // Each one says which layer failed and what to change. The raw reason travels in the
            // details, for the operator who wants it.
            var errorMessage = ex.StatusCode switch
            {
                System.Net.HttpStatusCode.Forbidden =>
                    $"Authenticated, but not allowed to list bucket '{bucket}'. Give Connapse's identity "
                    + "s3:ListBucket and s3:GetObject on it (the policy under the allowed locations does "
                    + "exactly that), or check the Role ARN.",
                System.Net.HttpStatusCode.Unauthorized =>
                    "Reached AWS, but it rejected Connapse's credential. Check the Access step on the AWS provider page.",
                System.Net.HttpStatusCode.NotFound =>
                    $"Authenticated, but no bucket '{bucket}' exists in {regionName}. Check the name, or clear "
                    + "the region so it is looked up from the bucket.",
                _ => $"S3 refused the request ({ex.ErrorCode ?? "unknown code"}): {ex.Message}"
            };

            return ConnectionTestResult.CreateFailure(
                errorMessage,
                new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
                    ["errorCode"] = ex.ErrorCode ?? "Unknown",
                    ["statusCode"] = (int)ex.StatusCode
                },
                stopwatch.Elapsed);
        }
        catch (AmazonClientException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "S3 connection test failed with client error");

            var message = ex.Message.Contains("credentials", StringComparison.OrdinalIgnoreCase)
                ? "Connapse has no AWS identity to test with. Set one up under Access on the AWS provider page."
                : $"Could not reach AWS: {ex.Message}";

            return ConnectionTestResult.CreateFailure(
                message,
                new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
                    ["errorType"] = ex.GetType().Name
                },
                stopwatch.Elapsed);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "S3 connection test timed out");

            return ConnectionTestResult.CreateFailure(
                $"AWS did not answer within {timeout.Value.TotalSeconds:F0} seconds. Check that this server can reach "
                + $"s3.{regionName}.amazonaws.com, then try again.",
                new Dictionary<string, object>
                {
                    ["error"] = "Timeout",
                    ["timeoutSeconds"] = timeout.Value.TotalSeconds
                },
                stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "S3 connection test failed with unexpected error");

            return ConnectionTestResult.CreateFailure(
                $"Unexpected error: {ex.Message}",
                new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
                    ["errorType"] = ex.GetType().Name
                },
                stopwatch.Elapsed);
        }
    }

    private static S3ConnectorConfig ExtractConfig(object settings)
    {
        if (settings is S3ConnectorConfig config)
            return config;

        if (settings is string json)
            return JsonSerializer.Deserialize<S3ConnectorConfig>(json, JsonOptions)
                ?? new S3ConnectorConfig();

        // Fall back to reflection for generic objects
        var type = settings.GetType();
        return new S3ConnectorConfig
        {
            BucketName = type.GetProperty("BucketName")?.GetValue(settings)?.ToString() ?? "",
            Region = type.GetProperty("Region")?.GetValue(settings)?.ToString() ?? "us-east-1",
            Prefix = type.GetProperty("Prefix")?.GetValue(settings)?.ToString(),
            RoleArn = type.GetProperty("RoleArn")?.GetValue(settings)?.ToString()
        };
    }
}
