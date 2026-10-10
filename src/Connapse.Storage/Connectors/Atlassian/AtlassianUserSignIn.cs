using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;

namespace Connapse.Storage.Connectors.Atlassian;

/// <summary>The Atlassian account a user signed in as.</summary>
public sealed record AtlassianUserAccount(string AccountId, string DisplayName, string? Email);

/// <summary>
/// The user sign-in that links an Atlassian account, through the link app an administrator stored.
/// Only <c>read:me</c> is requested and never <c>offline_access</c>: the access token is used once
/// to read who signed in and then goes out of scope. It is never stored and never logged.
/// </summary>
public sealed class AtlassianUserSignIn(IHttpClientFactory httpClients, IProviderCredentialStore credentials)
{
    public const string AuthorizeEndpoint = "https://auth.atlassian.com/authorize";
    public const string MeEndpoint = "https://api.atlassian.com/me";

    /// <summary>Atlassian's consent page for <c>read:me</c>, with the PKCE parameters when <paramref name="codeChallenge"/> is given.</summary>
    public static string AuthorizeUrl(string clientId, string redirectUri, string state, string? codeChallenge)
    {
        string url = AuthorizeEndpoint
            + "?audience=api.atlassian.com"
            + $"&client_id={Uri.EscapeDataString(clientId)}"
            + "&scope=read%3Ame"
            + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + $"&state={Uri.EscapeDataString(state)}"
            + "&response_type=code&prompt=consent";

        return codeChallenge is null
            ? url
            : url + $"&code_challenge={Uri.EscapeDataString(codeChallenge)}&code_challenge_method=S256";
    }

    /// <summary>The URL to send a user to, or null when no link app (with its secret) is stored.</summary>
    public async Task<string?> SignInUrlAsync(string redirectUri, string state, string? codeChallenge, CancellationToken ct = default)
    {
        var app = await credentials.GetAtlassianLinkAppAsync(ct);
        if (app is null || string.IsNullOrEmpty(await credentials.GetAtlassianLinkAppSecretAsync(ct)))
            return null;

        return AuthorizeUrl(app.ClientId, redirectUri, state, codeChallenge);
    }

    /// <summary>
    /// Trades a sign-in code for the account that signed in. Anything short of a token and an
    /// account with an id throws, so a caller that catches stores nothing.
    /// </summary>
    public async Task<AtlassianUserAccount> ResolveAsync(
        string code, string? codeVerifier, string redirectUri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var app = await credentials.GetAtlassianLinkAppAsync(ct);
        string? secret = await credentials.GetAtlassianLinkAppSecretAsync(ct);
        if (app is null || string.IsNullOrEmpty(secret))
            throw new AtlassianAuthException("No Atlassian link app is set up.");

        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = app.ClientId,
            ["client_secret"] = secret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
        };
        if (codeVerifier is not null)
            body["code_verifier"] = codeVerifier;

        using var http = httpClients.CreateClient(AtlassianApiClient.HttpClientName);
        using var exchanged = await http.PostAsJsonAsync(AtlassianTokenSource.TokenEndpoint, body, ct);
        if (!exchanged.IsSuccessStatusCode)
        {
            // Atlassian's error code tells a wrong secret (access_denied) from a stale code (invalid_grant).
            string reason = await ErrorReasonAsync(exchanged, ct);
            throw new AtlassianAuthException($"Atlassian did not accept the sign-in code (HTTP {(int)exchanged.StatusCode}{reason}).");
        }

        var grant = await exchanged.Content.ReadFromJsonAsync<TokenResponse>(ct);
        if (string.IsNullOrEmpty(grant?.AccessToken))
            throw new AtlassianAuthException("Atlassian's token endpoint returned no access token.");

        using var request = new HttpRequestMessage(HttpMethod.Get, MeEndpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", grant.AccessToken);
        using var who = await http.SendAsync(request, ct);
        if (!who.IsSuccessStatusCode)
            throw new AtlassianAuthException($"Atlassian did not say who signed in (HTTP {(int)who.StatusCode}).");

        var me = await who.Content.ReadFromJsonAsync<MeResponse>(ct);
        if (string.IsNullOrWhiteSpace(me?.AccountId))
            throw new AtlassianAuthException("Atlassian returned an account without an id.");

        string displayName = string.IsNullOrWhiteSpace(me.Name) ? me.AccountId : me.Name;
        return new AtlassianUserAccount(me.AccountId, displayName, string.IsNullOrWhiteSpace(me.Email) ? null : me.Email);
    }

    private static async Task<string> ErrorReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(ct);
            if (string.IsNullOrWhiteSpace(error?.Error))
                return "";
            string text = string.IsNullOrWhiteSpace(error.Description) ? error.Error : $"{error.Error}: {error.Description}";
            return ", " + LogSanitizer.Sanitize(text.Length > 200 ? text[..200] : text);
        }
        catch (Exception)
        {
            return "";
        }
    }

    private sealed record ErrorResponse(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? Description);

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string? AccessToken);

    private sealed record MeResponse(
        [property: JsonPropertyName("account_id")] string? AccountId,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("email")] string? Email);
}
