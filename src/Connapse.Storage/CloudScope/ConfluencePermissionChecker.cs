using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.Atlassian;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Asks Confluence, as the site's service account, whether an Atlassian account may read a piece of
/// content. Admits only an HTTP 200 whose <c>hasPermission</c> is the boolean <c>true</c>; every other
/// answer, and every failure to get one, is a denial.
/// </summary>
/// <remarks>
/// Answers are cached per <c>(cloudId, accountId, contentId)</c>: a definite allow or deny for
/// <see cref="AllowTtl"/>, a failure for <see cref="FailureTtl"/> so an outage is not hammered.
/// Cancellation is never cached and always propagates.
/// </remarks>
public sealed class ConfluencePermissionChecker(
    IHttpClientFactory http,
    AtlassianTokenSource tokens,
    IServiceScopeFactory scopes,
    IMemoryCache cache,
    ILogger<ConfluencePermissionChecker> logger)
{
    public static readonly TimeSpan AllowTtl = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(30);

    public Task<bool> CanReadAsync(string cloudId, string contentId, string accountId, CancellationToken ct) =>
        CanReadAsync(cloudId, contentId, accountId, NewSiteLookup(), ct);

    /// <summary>
    /// The same check, finding the site's connection through <paramref name="sites"/> so that many
    /// checks in one search look the connection up (and decrypt its secret) once per site.
    /// </summary>
    public async Task<bool> CanReadAsync(
        string cloudId, string contentId, string accountId, SiteLookup sites, CancellationToken ct)
    {
        // An empty or anonymous subject would ask what the public can see, not what this user can.
        if (string.IsNullOrWhiteSpace(accountId) || accountId.Trim().Equals("anonymous", StringComparison.OrdinalIgnoreCase))
            return false;

        // Both end up in the request path, so anything but a GUID site and a numeric id is refused.
        if (!Guid.TryParse(cloudId, out Guid site) || contentId.Length == 0 || !contentId.All(char.IsAsciiDigit))
            return false;
        cloudId = site.ToString("D");

        string key = $"atl:{cloudId}:{accountId}:{contentId}";
        if (cache.TryGetValue(key, out bool cached))
            return cached;

        (bool allowed, TimeSpan lifetime) = await CheckAsync(cloudId, contentId, accountId, sites, ct);
        cache.Set(key, allowed, lifetime);
        return allowed;
    }

    private async Task<(bool Allowed, TimeSpan Lifetime)> CheckAsync(
        string cloudId, string contentId, string accountId, SiteLookup sites, CancellationToken ct)
    {
        try
        {
            if (await sites.ClientForAsync(cloudId, ct) is not { } client)
                return (false, FailureTtl);

            object body = new { subject = new { type = "user", identifier = accountId }, operation = "read" };
            using var response = await client.PostAsync($"rest/api/content/{contentId}/permission/check", body, ct);

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
                return (false, AllowTtl); // Confluence's definite "no such content for this check"

            if (response.StatusCode != HttpStatusCode.OK)
            {
                logger.LogWarning("Confluence permission check on site {CloudId} answered {Status}; denying content {ContentId}",
                    cloudId, (int)response.StatusCode, contentId);
                return (false, FailureTtl);
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("hasPermission", out var has)
                && has.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return (has.ValueKind == JsonValueKind.True, AllowTtl);

            logger.LogWarning("Confluence permission check on site {CloudId} gave no boolean answer; denying content {ContentId}",
                cloudId, contentId);
            return (false, FailureTtl);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Rate limits, rejected credentials, an unreadable secret, timeouts, malformed answers.
            logger.LogWarning(ex, "Confluence permission check on site {CloudId} failed; denying content {ContentId}",
                cloudId, contentId);
            return (false, FailureTtl);
        }
    }

    /// <summary>
    /// A lookup that remembers each site's connection for as long as the caller holds it. Hold one
    /// for a single search only: it keeps the decrypted secret inside the clients it hands out.
    /// </summary>
    public SiteLookup NewSiteLookup() => new(LookUpClientAsync);

    /// <summary>A client for the connection that reads <paramref name="cloudId"/>, or null when none does or its secret is gone.</summary>
    private async Task<AtlassianApiClient?> LookUpClientAsync(string cloudId, CancellationToken ct)
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

            return new AtlassianApiClient(http.CreateClient(AtlassianApiClient.HttpClientName), tokens, site, secret);
        }

        logger.LogWarning("No Atlassian connection reads site {CloudId}; denying its documents", cloudId);
        return null;
    }

    /// <summary>
    /// Each site's connection, looked up at most once. Concurrent checks for one site share the
    /// lookup, and its outcome: a missing connection or an unreadable secret denies them all.
    /// </summary>
    public sealed class SiteLookup
    {
        private readonly Func<string, CancellationToken, Task<AtlassianApiClient?>> _lookUp;
        private readonly ConcurrentDictionary<string, Lazy<Task<AtlassianApiClient?>>> _sites = new(StringComparer.Ordinal);

        internal SiteLookup(Func<string, CancellationToken, Task<AtlassianApiClient?>> lookUp) => _lookUp = lookUp;

        internal Task<AtlassianApiClient?> ClientForAsync(string cloudId, CancellationToken ct) =>
            _sites.GetOrAdd(cloudId, id => new Lazy<Task<AtlassianApiClient?>>(() => _lookUp(id, ct))).Value;
    }
}
