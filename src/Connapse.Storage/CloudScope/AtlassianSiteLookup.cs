using System.Collections.Concurrent;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.Atlassian;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Each site's connection, looked up at most once. Concurrent checks for one site share the
/// lookup, and its outcome: a missing connection or an unreadable secret denies them all. Hold one
/// for a single search only: it keeps the decrypted secret for as long as it lives.
/// </summary>
public sealed class AtlassianSiteLookup(
    IServiceScopeFactory scopes, IHttpClientFactory http, AtlassianTokenSource tokens, ILogger logger)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<(AtlassianSite Site, string Secret)?>>> _sites = new(StringComparer.Ordinal);

    /// <summary>The connection that reads <paramref name="cloudId"/>, or null when none does or its secret is gone.</summary>
    internal Task<(AtlassianSite Site, string Secret)?> SiteForAsync(string cloudId, CancellationToken ct) =>
        _sites.GetOrAdd(cloudId, id => new Lazy<Task<(AtlassianSite, string)?>>(() => LookUpAsync(id, ct))).Value;

    /// <summary>A client for one product of a site <see cref="SiteForAsync"/> found.</summary>
    public AtlassianApiClient ClientFor(AtlassianSite site, string secret, AtlassianProduct product) =>
        new(http.CreateClient(AtlassianApiClient.HttpClientName), tokens, site, secret, product: product);

    private async Task<(AtlassianSite Site, string Secret)?> LookUpAsync(string cloudId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();

        foreach (var connection in await connections.ListAsync(take: int.MaxValue, ct: ct))
        {
            if (connection.Provider != ConnectionProvider.Atlassian
                || AtlassianSite.FromConfigJson(connection.ConfigJson) is not { } site
                || !string.Equals(site.CloudId, cloudId, StringComparison.OrdinalIgnoreCase))
                continue;

            string? secret = await connections.GetSecretAsync(connection.Id, ct);
            if (string.IsNullOrEmpty(secret))
            {
                logger.LogWarning("Atlassian connection {ConnectionId} has no readable secret; denying checks on site {CloudId}",
                    connection.Id, cloudId);
                return null;
            }

            return (site, secret);
        }

        logger.LogWarning("No Atlassian connection reads site {CloudId}; denying its documents", cloudId);
        return null;
    }
}
