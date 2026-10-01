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
            entry.ExpiresAt = time.GetUtcNow() + lifetime - RefreshMargin;
            entry.Token = token;
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
        if (_entries.TryGetValue((site.CloudId, site.ClientId), out var entry) && entry.Token == rejected)
            entry.Token = null;
    }

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
        public volatile string? Token;
        private long _expiresAtTicks;
        public DateTimeOffset ExpiresAt
        {
            get => new(Volatile.Read(ref _expiresAtTicks), TimeSpan.Zero);
            set => Volatile.Write(ref _expiresAtTicks, value.UtcTicks);
        }

        public string? Fresh(TimeProvider time) =>
            Token is { } token && time.GetUtcNow() < ExpiresAt ? token : null;
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
