using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Services;
using Connapse.Identity.Stores;
using Microsoft.Extensions.Logging;

namespace Connapse.Web.Services;

public class CloudScopeService(
    IEnumerable<ICloudIdentityProvider> providers,
    IConnectorScopeCache cache,
    ICloudIdentityService identityService,
    ICloudIdentityStore identityStore,
    ILogger<CloudScopeService> logger) : ICloudScopeService
{
    private static readonly TimeSpan AllowTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DenyTtl = TimeSpan.FromMinutes(5);

    public async Task<CloudScopeResult?> GetScopesAsync(
        Guid userId,
        Container container,
        CancellationToken ct = default)
    {
        // Only enforce for cloud connectors — local connectors use role-level RBAC
        var cloudProvider = container.ConnectorType switch
        {
            ConnectorType.S3 => CloudProvider.AWS,
            ConnectorType.AzureBlob => CloudProvider.Azure,
            _ => (CloudProvider?)null
        };

        if (cloudProvider is null)
            return null;

        var containerId = Guid.Parse(container.Id);

        // Check cache
        var cached = await cache.GetAsync(userId, containerId);
        if (cached is not null)
        {
            logger.LogDebug("Scope cache hit for user {UserId} + container {ContainerId}", userId, containerId);
            return cached;
        }

        // Load the user's cloud identity
        var identity = await identityService.GetAsync(userId, cloudProvider.Value, ct);
        if (identity is null)
        {
            var denyResult = CloudScopeResult.Deny(
                $"No {cloudProvider.Value} identity linked to your account. " +
                $"Visit Profile > Cloud Identities to connect your {cloudProvider.Value} account.");
            await cache.SetAsync(userId, containerId, denyResult, DenyTtl);
            return denyResult;
        }

        // Dispatch to the appropriate provider
        var provider = providers.FirstOrDefault(p => p.Provider == cloudProvider.Value);
        if (provider is null)
        {
            logger.LogError("No ICloudIdentityProvider registered for {Provider}", cloudProvider.Value);
            return CloudScopeResult.Deny($"Internal error: scope provider for {cloudProvider.Value} not registered.");
        }

        var result = await provider.DiscoverScopesAsync(identity.Data, container, ct);

        // Cache the result
        var ttl = result.HasAccess ? AllowTtl : DenyTtl;
        await cache.SetAsync(userId, containerId, result, ttl);

        // Update LastUsedAt on successful access
        if (result.HasAccess)
        {
            try { await identityStore.UpdateLastUsedAsync(identity.Id, ct); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to update LastUsedAt for identity {Id}", identity.Id);
            }
        }

        logger.LogInformation(
            "Scope discovery for user {UserId} + container {ContainerId}: HasAccess={HasAccess}, Prefixes=[{Prefixes}]",
            userId, containerId, result.HasAccess, string.Join(", ", result.AllowedPrefixes));

        return result;
    }
}
