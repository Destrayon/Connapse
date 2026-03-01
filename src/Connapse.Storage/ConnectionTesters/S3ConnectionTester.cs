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
using Connapse.Storage.Connectors;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>
/// Tests connectivity to an AWS S3 bucket using DefaultAWSCredentials (IAM roles, env vars, instance profile).
/// Optionally assumes a role via STS if RoleArn is configured.
/// </summary>
public class S3ConnectionTester : IConnectionTester
{
    private readonly ILogger<S3ConnectionTester> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public S3ConnectionTester(ILogger<S3ConnectionTester> logger)
    {
        _logger = logger;
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(
        object settings,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        timeout ??= TimeSpan.FromSeconds(15);

        try
        {
            var config = ExtractConfig(settings);

            if (string.IsNullOrWhiteSpace(config.BucketName))
            {
                return ConnectionTestResult.CreateFailure(
                    "Bucket name is required",
                    new Dictionary<string, object> { ["error"] = "Missing BucketName in config" });
            }

            _logger.LogDebug("Testing S3 connection to bucket {Bucket} in region {Region}",
                config.BucketName, config.Region);

            var region = RegionEndpoint.GetBySystemName(config.Region);

            IAmazonS3 s3Client;
            if (!string.IsNullOrWhiteSpace(config.RoleArn))
            {
                using var stsClient = new AmazonSecurityTokenServiceClient(region);
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
                s3Client = new AmazonS3Client(new AmazonS3Config
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
            var message = $"Connected to S3 bucket '{config.BucketName}' in {config.Region}"
                + (objectCount > 0
                    ? $" ({objectCount}{(hasMore ? "+" : "")} object{(objectCount != 1 ? "s" : "")} found{(string.IsNullOrEmpty(config.Prefix) ? "" : $" under prefix '{config.Prefix}'")})"
                    : $" (empty{(string.IsNullOrEmpty(config.Prefix) ? "" : $" under prefix '{config.Prefix}'")})");

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

            var errorMessage = ex.StatusCode switch
            {
                System.Net.HttpStatusCode.Forbidden => "Access denied: Insufficient permissions on this bucket",
                System.Net.HttpStatusCode.Unauthorized => "Authentication failed: No valid AWS credentials found",
                System.Net.HttpStatusCode.NotFound => "Bucket not found: Check the bucket name and region",
                _ => $"S3 error: {ex.Message}"
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
                ? "No AWS credentials found. Configure IAM role, environment variables (AWS_ACCESS_KEY_ID), or ~/.aws/credentials."
                : $"AWS client error: {ex.Message}";

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
                $"Connection timed out after {timeout.Value.TotalSeconds:F1}s",
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
