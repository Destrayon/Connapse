using System.Text.Json;
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
        Source source,
        Connection connection,
        CancellationToken ct = default)
    {
        if (source.ConnectionId != connection.Id)
            throw new ArgumentException(
                $"Connection '{connection.Id}' does not own source '{source.Id}'.", nameof(connection));

        // Only enforce for cloud providers — a Filesystem source is local, so role-level
        // RBAC is the whole story and there is no external IAM to consult.
        var cloudProvider = connection.Provider switch
        {
            ConnectionProvider.S3 => CloudProvider.AWS,
            ConnectionProvider.AzureBlob => CloudProvider.Azure,
            _ => (CloudProvider?)null
        };

        if (cloudProvider is null)
            return null;

        // Cached per source, not per connection: two sources sharing one connection point at
        // different buckets, and a user's access to one says nothing about the other.
        var cached = await cache.GetAsync(userId, source.Id);
        if (cached is not null)
        {
            logger.LogDebug("Scope cache hit for user {UserId} + source {SourceId}", userId, source.Id);
            return cached;
        }

        var identity = await identityService.GetAsync(userId, cloudProvider.Value, ct);
        if (identity is null)
        {
            var denyResult = CloudScopeResult.Deny(
                $"No {cloudProvider.Value} identity linked to your account. " +
                $"Visit Profile > Cloud Identities to connect your {cloudProvider.Value} account.");
            await cache.SetAsync(userId, source.Id, denyResult, DenyTtl);
            return denyResult;
        }

        var provider = providers.FirstOrDefault(p => p.Provider == cloudProvider.Value);
        if (provider is null)
        {
            logger.LogError("No ICloudIdentityProvider registered for {Provider}", cloudProvider.Value);
            return CloudScopeResult.Deny($"Internal error: scope provider for {cloudProvider.Value} not registered.");
        }

        var result = await provider.DiscoverScopesAsync(
            identity.Data, RecombineConnectorConfig(source, connection), ct);

        var ttl = result.HasAccess ? AllowTtl : DenyTtl;
        await cache.SetAsync(userId, source.Id, result, ttl);

        if (result.HasAccess)
        {
            try { await identityStore.UpdateLastUsedAsync(identity.Id, ct); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to update LastUsedAt for identity {Id}", identity.Id);
            }
        }

        logger.LogInformation(
            "Scope discovery for user {UserId} + source {SourceId}: HasAccess={HasAccess}, Prefixes=[{Prefixes}]",
            userId, source.Id, result.HasAccess, string.Join(", ", result.AllowedPrefixes));

        return result;
    }

    /// <summary>
    /// Flattens a connection's credential and its source's scope back into the single object
    /// a container's <c>connector_config</c> column used to hold, which is the shape the
    /// identity providers read. The source wins on conflict: the connection says which account
    /// to authenticate as, the source says which bucket within it, and a connection that also
    /// named a bucket must not be able to widen the source past its own scope.
    /// </summary>
    private static string RecombineConnectorConfig(Source source, Connection connection)
    {
        var merged = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in new[] { connection.ConfigJson, source.ScopeJson })
        {
            if (string.IsNullOrWhiteSpace(part)) continue;

            using var doc = JsonDocument.Parse(part);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;

            foreach (var property in doc.RootElement.EnumerateObject())
                merged[property.Name] = property.Value.Clone();
        }

        return JsonSerializer.Serialize(merged);
    }
}
