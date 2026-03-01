using Azure.Identity;
using Azure.Storage.Blobs;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Azure scope discovery. Verifies the user has a linked Azure identity and that the
/// service credential can reach the container. Returns access scoped to the container's
/// configured prefix. Full Azure RBAC prefix enumeration deferred (requires Azure.ResourceManager
/// and subscription/resource-group metadata in config).
/// </summary>
public class AzureIdentityProvider(ILogger<AzureIdentityProvider> logger) : ICloudIdentityProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public CloudProvider Provider => CloudProvider.Azure;

    public async Task<CloudScopeResult> DiscoverScopesAsync(
        CloudIdentityData identityData,
        Container container,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(identityData.ObjectId))
        {
            return CloudScopeResult.Deny(
                "Azure identity not linked. Connect your Azure account via Profile > Cloud Identities.");
        }

        if (string.IsNullOrEmpty(container.ConnectorConfig))
            return CloudScopeResult.Deny("Container has no connector configuration.");

        var config = JsonSerializer.Deserialize<AzureBlobConnectorConfig>(container.ConnectorConfig, JsonOptions);
        if (config is null || string.IsNullOrEmpty(config.StorageAccountName))
            return CloudScopeResult.Deny("Container connector configuration is invalid.");

        try
        {
            var credentialOptions = new DefaultAzureCredentialOptions();
            if (!string.IsNullOrWhiteSpace(config.ManagedIdentityClientId))
                credentialOptions.ManagedIdentityClientId = config.ManagedIdentityClientId;

            var credential = new DefaultAzureCredential(credentialOptions);
            var serviceUri = new Uri($"https://{config.StorageAccountName}.blob.core.windows.net");
            var serviceClient = new BlobServiceClient(serviceUri, credential);
            var containerClient = serviceClient.GetBlobContainerClient(config.ContainerName);

            // Verify read access with a lightweight existence check
            var exists = await containerClient.ExistsAsync(ct);
            if (!exists.Value)
            {
                return CloudScopeResult.Deny(
                    $"Azure Blob container '{config.ContainerName}' not accessible.");
            }

            logger.LogInformation(
                "Azure identity {ObjectId} granted access to {Account}/{Container}. " +
                "Full RBAC prefix discovery deferred to a future session",
                identityData.ObjectId, config.StorageAccountName, config.ContainerName);

            // Grant access to the configured prefix (or full container)
            var prefix = string.IsNullOrEmpty(config.Prefix)
                ? "/"
                : "/" + config.Prefix.TrimEnd('/') + "/";

            return CloudScopeResult.Allow([prefix]);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Azure scope discovery failed for ObjectId {ObjectId}", identityData.ObjectId);
            return CloudScopeResult.Deny($"Azure access verification failed: {ex.Message}");
        }
    }
}
