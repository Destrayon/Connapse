using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Connapse.Storage.Connectors.Atlassian;

/// <summary>
/// Service-account access tokens, one per <c>(cloudId, clientId)</c>. A token is reused until five
/// minutes before it expires; callers that arrive while one is being fetched share that fetch
/// rather than each posting their own. Neither the secret nor a token is ever logged.
/// </summary>
public sealed class AtlassianTokenSource(IHttpClientFactory httpClients, TimeProvider time)
{
    public const string TokenEndpoint = "https://auth.atlassian.com/oauth/token";

    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(300);

    private readonly ConcurrentDictionary<(string CloudId, string ClientId), Entry> _entries = new();

    public async Task<string> GetTokenAsync(AtlassianSite site, string clientSecret, CancellationToken ct)
    {
        var entry = _entries.GetOrAdd((site.CloudId, site.ClientId), _ => new Entry());

        if (entry.Fresh(time) is { } cached)
            return cached;

        await entry.Gate.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed while this one waited on the gate.
            if (entry.Fresh(time) is { } refreshed)
                return refreshed;

            var (token, lifetime) = await FetchAsync(site, clientSecret, ct);
            Volatile.Write(ref entry.Current, new Issued(token, time.GetUtcNow() + lifetime - RefreshMargin));
            return token;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>
    /// Drops the cached token, but only if it is still <paramref name="rejected"/>: a caller whose
    /// 401 arrives after another already replaced the token must not throw the new one away.
    /// </summary>
    public void Invalidate(AtlassianSite site, string rejected)
    {
        if (!_entries.TryGetValue((site.CloudId, site.ClientId), out var entry))
            return;

        // Clear only the exact token record that was observed; if a refresh has published a new
        // one in the meantime, the compare-and-swap fails and the new token stays.
        Issued? observed = Volatile.Read(ref entry.Current);
        if (observed?.Token != rejected)
            return;

        BetweenInvalidateCompareAndClear?.Invoke();
        Interlocked.CompareExchange(ref entry.Current, null, observed);
    }

    /// <summary>Test seam: runs after <see cref="Invalidate"/> has matched the token and before it clears it.</summary>
    internal Action? BetweenInvalidateCompareAndClear { get; set; }

    private async Task<(string Token, TimeSpan Lifetime)> FetchAsync(
        AtlassianSite site, string clientSecret, CancellationToken ct)
    {
        using var http = httpClients.CreateClient(AtlassianApiClient.HttpClientName);
        using var response = await http.PostAsync(
            TokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = site.ClientId,
                ["client_secret"] = clientSecret,
            }),
            ct);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new AtlassianRateLimitedException(AtlassianApiClient.RetryAfterOf(response, time));

        if ((int)response.StatusCode is >= 300 and < 400)
            throw new AtlassianAuthException(
                $"Atlassian's token endpoint answered with a redirect (HTTP {(int)response.StatusCode}); "
                + "Connapse does not follow redirects with service account credentials.");

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new AtlassianAuthException(
                $"Atlassian rejected the service account credentials (HTTP {(int)response.StatusCode}). "
                + "Check the client id and secret.");

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
        if (string.IsNullOrEmpty(body?.AccessToken))
            throw new AtlassianAuthException("Atlassian's token endpoint returned no access token.");

        return (body.AccessToken, TimeSpan.FromSeconds(body.ExpiresIn > 0 ? body.ExpiresIn : 3600));
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>The cached token and its expiry, replaced as one unit so they never tear.</summary>
        public Issued? Current;

        public string? Fresh(TimeProvider time) =>
            Volatile.Read(ref Current) is { } issued && time.GetUtcNow() < issued.ExpiresAt ? issued.Token : null;
    }

    private sealed record Issued(string Token, DateTimeOffset ExpiresAt);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
