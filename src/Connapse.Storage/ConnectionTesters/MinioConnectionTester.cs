using System.Diagnostics;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>
/// Tests connectivity to MinIO/S3 object storage.
/// Verifies credentials and lists buckets to ensure proper access.
/// </summary>
public class MinioConnectionTester : IConnectionTester
{
    private readonly ILogger<MinioConnectionTester> _logger;

    public MinioConnectionTester(ILogger<MinioConnectionTester> logger)
    {
        _logger = logger;
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(
        object settings,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        timeout ??= TimeSpan.FromSeconds(10);

        try
        {
            // Extract MinIO settings
            var (endpoint, accessKey, secretKey, bucketName, useSSL) = ExtractMinioSettings(settings);

            // Validate required settings
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return ConnectionTestResult.CreateFailure(
                    "MinioEndpoint is required",
                    new Dictionary<string, object> { ["error"] = "Missing MinioEndpoint in settings" });
            }

            if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
            {
                return ConnectionTestResult.CreateFailure(
                    "MinioAccessKey and MinioSecretKey are required",
                    new Dictionary<string, object> { ["error"] = "Missing credentials in settings" });
            }

            _logger.LogDebug("Testing MinIO connection to {Endpoint}", endpoint);

            // Create S3 client with MinIO configuration
            var config = new AmazonS3Config
            {
                ServiceURL = $"{(useSSL ? "https" : "http")}://{endpoint}",
                ForcePathStyle = true, // Required for MinIO
                Timeout = timeout
            };

            var credentials = new BasicAWSCredentials(accessKey, secretKey);
            using var s3Client = new AmazonS3Client(credentials, config);

            // Test 1: List all buckets to verify credentials
            var listBucketsResponse = await s3Client.ListBucketsAsync(ct);

            // Test 2: Check if the configured bucket exists
            bool bucketExists = listBucketsResponse.Buckets.Any(b =>
                b.BucketName.Equals(bucketName, StringComparison.OrdinalIgnoreCase));

            stopwatch.Stop();

            var bucketCount = listBucketsResponse.Buckets.Count;
            var message = bucketExists
                ? $"Connected to MinIO at {endpoint} (bucket '{bucketName}' exists, {bucketCount} total bucket{(bucketCount != 1 ? "s" : "")})"
                : $"Connected to MinIO at {endpoint} (bucket '{bucketName}' not found, {bucketCount} bucket{(bucketCount != 1 ? "s" : "")} available)";

            return ConnectionTestResult.CreateSuccess(
                message,
                new Dictionary<string, object>
                {
                    ["endpoint"] = endpoint,
                    ["useSSL"] = useSSL,
                    ["bucketName"] = bucketName ?? "(none)",
                    ["bucketExists"] = bucketExists,
                    ["totalBuckets"] = bucketCount,
                    ["buckets"] = listBucketsResponse.Buckets.Select(b => b.BucketName).ToList()
                },
                stopwatch.Elapsed);
        }
        catch (AmazonS3Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "MinIO connection test failed with S3 error");

            var errorMessage = ex.StatusCode switch
            {
                System.Net.HttpStatusCode.Forbidden => "Access denied: Invalid credentials or insufficient permissions",
                System.Net.HttpStatusCode.Unauthorized => "Authentication failed: Invalid access key or secret key",
                _ => $"S3 error: {ex.Message}"
            };

            return ConnectionTestResult.CreateFailure(
                errorMessage,
                new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
                    ["errorCode"] = ex.ErrorCode,
                    ["statusCode"] = (int)ex.StatusCode
                },
                stopwatch.Elapsed);
        }
        catch (AmazonClientException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "MinIO connection test failed with client error");

            return ConnectionTestResult.CreateFailure(
                $"Connection failed: {ex.Message}",
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
            _logger.LogWarning(ex, "MinIO connection test timed out");

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
            _logger.LogError(ex, "MinIO connection test failed with unexpected error");

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

    private static (string? endpoint, string? accessKey, string? secretKey, string? bucketName, bool useSSL) ExtractMinioSettings(object settings)
    {
        // Use reflection to extract MinIO properties from StorageSettings
        var type = settings.GetType();
        var endpoint = type.GetProperty("MinioEndpoint")?.GetValue(settings)?.ToString();
        var accessKey = type.GetProperty("MinioAccessKey")?.GetValue(settings)?.ToString();
        var secretKey = type.GetProperty("MinioSecretKey")?.GetValue(settings)?.ToString();
        var bucketName = type.GetProperty("MinioBucketName")?.GetValue(settings)?.ToString();
        var useSSL = type.GetProperty("MinioUseSSL")?.GetValue(settings) as bool? ?? false;

        return (endpoint, accessKey, secretKey, bucketName, useSSL);
    }
}
