using System.Diagnostics;
using System.Text.Json;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>
/// Tests connectivity to an Azure Blob Storage container using DefaultAzureCredential.
/// Verifies read access by listing blobs.
/// </summary>
public class AzureBlobConnectionTester : IConnectionTester
{
    private readonly ILogger<AzureBlobConnectionTester> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public AzureBlobConnectionTester(ILogger<AzureBlobConnectionTester> logger)
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

            if (string.IsNullOrWhiteSpace(config.StorageAccountName))
            {
                return ConnectionTestResult.CreateFailure(
                    "Storage account name is required",
                    new Dictionary<string, object> { ["error"] = "Missing StorageAccountName in config" });
            }

            if (string.IsNullOrWhiteSpace(config.ContainerName))
            {
                return ConnectionTestResult.CreateFailure(
                    "Container name is required",
                    new Dictionary<string, object> { ["error"] = "Missing ContainerName in config" });
            }

            _logger.LogDebug("Testing Azure Blob connection to {Account}/{Container}",
                config.StorageAccountName, config.ContainerName);

            var credentialOptions = new DefaultAzureCredentialOptions();
            if (!string.IsNullOrWhiteSpace(config.ManagedIdentityClientId))
                credentialOptions.ManagedIdentityClientId = config.ManagedIdentityClientId;

            var credential = new DefaultAzureCredential(credentialOptions);
            var serviceUri = new Uri($"https://{config.StorageAccountName}.blob.core.windows.net");
            var serviceClient = new BlobServiceClient(serviceUri, credential);
            var containerClient = serviceClient.GetBlobContainerClient(config.ContainerName);

            // Test: list up to 5 blobs to verify read access
            int blobCount = 0;
            bool hasMore = false;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout.Value);

            await foreach (var blob in containerClient.GetBlobsAsync(
                prefix: config.Prefix,
                cancellationToken: cts.Token))
            {
                blobCount++;
                if (blobCount >= 5)
                {
                    hasMore = true;
                    break;
                }
            }

            stopwatch.Stop();

            var message = $"Connected to Azure Blob '{config.StorageAccountName}/{config.ContainerName}'"
                + (blobCount > 0
                    ? $" ({blobCount}{(hasMore ? "+" : "")} blob{(blobCount != 1 ? "s" : "")} found{(string.IsNullOrEmpty(config.Prefix) ? "" : $" under prefix '{config.Prefix}'")})"
                    : $" (empty{(string.IsNullOrEmpty(config.Prefix) ? "" : $" under prefix '{config.Prefix}'")})");

            return ConnectionTestResult.CreateSuccess(
                message,
                new Dictionary<string, object>
                {
                    ["storageAccountName"] = config.StorageAccountName,
                    ["containerName"] = config.ContainerName,
                    ["prefix"] = config.Prefix ?? "(none)",
                    ["blobsFound"] = blobCount,
                    ["hasMore"] = hasMore,
                    ["usedManagedIdentity"] = !string.IsNullOrWhiteSpace(config.ManagedIdentityClientId)
                },
                stopwatch.Elapsed);
        }
        catch (RequestFailedException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Azure Blob connection test failed with request error");

            var errorMessage = ex.Status switch
            {
                403 => "Access denied: Insufficient permissions. Ensure the identity has 'Storage Blob Data Reader' role.",
                401 => "Authentication failed: No valid Azure credentials found",
                404 => "Container not found: Check the storage account and container name",
                _ => $"Azure error ({ex.Status}): {ex.Message}"
            };

            return ConnectionTestResult.CreateFailure(
                errorMessage,
                new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
                    ["errorCode"] = ex.ErrorCode ?? "Unknown",
                    ["statusCode"] = ex.Status
                },
                stopwatch.Elapsed);
        }
        catch (CredentialUnavailableException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Azure Blob connection test failed — no credential available");

            return ConnectionTestResult.CreateFailure(
                "No Azure credentials found. Run 'az login', configure managed identity, or set AZURE_CLIENT_ID/AZURE_TENANT_ID/AZURE_CLIENT_SECRET environment variables.",
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
            _logger.LogWarning(ex, "Azure Blob connection test timed out");

            return ConnectionTestResult.CreateFailure(
                $"Connection timed out after {timeout.Value.TotalSeconds:F1}s. DefaultAzureCredential tries multiple auth methods — this can be slow without configured credentials.",
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
            _logger.LogError(ex, "Azure Blob connection test failed with unexpected error");

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

    private static AzureBlobConnectorConfig ExtractConfig(object settings)
    {
        if (settings is AzureBlobConnectorConfig config)
            return config;

        if (settings is string json)
            return JsonSerializer.Deserialize<AzureBlobConnectorConfig>(json, JsonOptions)
                ?? new AzureBlobConnectorConfig();

        // Fall back to reflection for generic objects
        var type = settings.GetType();
        return new AzureBlobConnectorConfig
        {
            StorageAccountName = type.GetProperty("StorageAccountName")?.GetValue(settings)?.ToString() ?? "",
            ContainerName = type.GetProperty("ContainerName")?.GetValue(settings)?.ToString() ?? "",
            Prefix = type.GetProperty("Prefix")?.GetValue(settings)?.ToString(),
            ManagedIdentityClientId = type.GetProperty("ManagedIdentityClientId")?.GetValue(settings)?.ToString()
        };
    }
}
