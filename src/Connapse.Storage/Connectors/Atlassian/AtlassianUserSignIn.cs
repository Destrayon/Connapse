using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;

namespace Connapse.Storage.Connectors.Atlassian;

/// <summary>The Atlassian account a user signed in as: its id only, never the name or email /me also returns.</summary>
public sealed record AtlassianUserAccount(string AccountId);

/// <summary>What Atlassian said about a linking app's client ID and secret.</summary>
public enum AtlassianLinkAppCheck
{
    /// <summary>Atlassian accepted the client ID and secret.</summary>
    Accepted,

    /// <summary>Atlassian refused the client ID or the secret.</summary>
    Rejected,

    /// <summary>Atlassian could not be reached, or answered in a way that says neither.</summary>
    Unknown,
}

/// <summary>Atlassian refused the stored linking app's client ID or secret during a sign-in.</summary>
public sealed class AtlassianLinkAppRejectedException()
    : Exception("Atlassian rejected the linking app's client ID or secret.");

/// <summary>An administrator turned Atlassian sign-in off, so no new account can be linked.</summary>
public sealed class AtlassianSignInOffException()
    : Exception("Atlassian sign-in is turned off.");

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
    /// <exception cref="AtlassianSignInOffException">An app is stored but sign-in is turned off.</exception>
    public async Task<string?> SignInUrlAsync(string redirectUri, string state, string? codeChallenge, CancellationToken ct = default)
    {
        var app = await credentials.GetAtlassianLinkAppAsync(ct);
        if (app is null || string.IsNullOrEmpty(await credentials.GetAtlassianLinkAppSecretAsync(ct)))
            return null;
        if (!app.SignInEnabled)
            throw new AtlassianSignInOffException();

        return AuthorizeUrl(app.ClientId, redirectUri, state, codeChallenge);
    }

    /// <summary>The code sent when checking an app: never one Atlassian issued, so it is always refused.</summary>
    public const string CheckCode = "connapse-app-check-not-a-real-code";

    /// <summary>
    /// Asks Atlassian whether it accepts a client ID and secret, without anyone signing in: a code
    /// exchange with a code that cannot be valid. Atlassian checks the client first, so
    /// <c>invalid_grant</c> (a bad code) means the pair was accepted, and <c>access_denied</c>,
    /// <c>unauthorized_client</c> or a 401 means it was not.
    /// </summary>
    public async Task<AtlassianLinkAppCheck> CheckAppAsync(
        string clientId, string clientSecret, string redirectUri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);

        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId.Trim(),
            ["client_secret"] = clientSecret.Trim(),
            ["code"] = CheckCode,
            ["redirect_uri"] = redirectUri,
        };

        try
        {
            using var http = httpClients.CreateClient(AtlassianApiClient.HttpClientName);
            using var response = await http.PostAsJsonAsync(AtlassianTokenSource.TokenEndpoint, body, ct);
            if (response.IsSuccessStatusCode)
                return AtlassianLinkAppCheck.Accepted;

            return Classify(response.StatusCode, (await ErrorAsync(response, ct))?.Error);
        }
        catch (HttpRequestException)
        {
            return AtlassianLinkAppCheck.Unknown;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return AtlassianLinkAppCheck.Unknown; // timed out
        }
    }

    /// <summary>Checks the stored app, and records the answer when Atlassian gave one.</summary>
    /// <returns>Null when no app (with its secret) is stored.</returns>
    public async Task<AtlassianLinkAppCheck?> CheckStoredAppAsync(string redirectUri, CancellationToken ct = default)
    {
        var app = await credentials.GetAtlassianLinkAppAsync(ct);
        string? secret = await credentials.GetAtlassianLinkAppSecretAsync(ct);
        if (app is null || string.IsNullOrEmpty(secret))
            return null;

        var result = await CheckAppAsync(app.ClientId, secret, redirectUri, ct);
        if (result != AtlassianLinkAppCheck.Unknown)
            await credentials.RecordAtlassianLinkAppCheckAsync(app.ClientId, result == AtlassianLinkAppCheck.Accepted, DateTime.UtcNow, ct);
        return result;
    }

    /// <summary>Reads a token endpoint refusal: <c>invalid_grant</c> is about the code, the rest about the client.</summary>
    internal static AtlassianLinkAppCheck Classify(System.Net.HttpStatusCode status, string? error) => error switch
    {
        "invalid_grant" => AtlassianLinkAppCheck.Accepted,
        "access_denied" or "unauthorized_client" or "invalid_client" => AtlassianLinkAppCheck.Rejected,
        _ when status == System.Net.HttpStatusCode.Unauthorized => AtlassianLinkAppCheck.Rejected,
        _ => AtlassianLinkAppCheck.Unknown,
    };

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
        if (!app.SignInEnabled)
            throw new AtlassianSignInOffException();

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
            var error = await ErrorAsync(exchanged, ct);
            string reason = ErrorReason(error);
            if (Classify(exchanged.StatusCode, error?.Error) == AtlassianLinkAppCheck.Rejected)
            {
                // The administrator's page shows this as a failed check, so a revoked secret is not silent.
                await credentials.RecordAtlassianLinkAppCheckAsync(app.ClientId, accepted: false, DateTime.UtcNow, ct);
                throw new AtlassianLinkAppRejectedException();
            }
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

        return new AtlassianUserAccount(me.AccountId);
    }

    private static string ErrorReason(ErrorResponse? error)
    {
        if (string.IsNullOrWhiteSpace(error?.Error))
            return "";
        string text = string.IsNullOrWhiteSpace(error.Description) ? error.Error : $"{error.Error}: {error.Description}";
        return ", " + LogSanitizer.Sanitize(text.Length > 200 ? text[..200] : text);
    }

    /// <summary>The token endpoint's error body, or null when it has none. Read once: the body is a stream.</summary>
    private static async Task<ErrorResponse?> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ErrorResponse>(ct);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed record ErrorResponse(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? Description);

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string? AccessToken);

    private sealed record MeResponse([property: JsonPropertyName("account_id")] string? AccountId);
}
