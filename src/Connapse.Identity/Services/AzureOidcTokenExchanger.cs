using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Connapse.Core;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Connapse.Identity.Services;

/// <summary>
/// Redeems an Entra ID authorization code for an id_token, authenticating the token request with
/// a signed client-assertion JWT (private_key_jwt) rather than a client secret — Connapse never
/// holds an Entra client secret, only the certificate configured in <see
/// cref="AzureAdSignInSettings"/>.
/// </summary>
public sealed class AzureOidcTokenExchanger(
    HttpClient httpClient,
    IOptionsMonitor<AzureAdSignInSettings> options) : IOidcTokenExchanger
{
    private const string ClientAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
    private static readonly JsonWebTokenHandler Handler = new();

    public async Task<string> ExchangeAsync(string code, string codeVerifier, CancellationToken ct)
    {
        AzureAdSignInSettings settings = options.CurrentValue;
        if (!settings.IsConfigured)
            throw new InvalidOperationException("Azure AD sign-in is not configured.");

        string tokenEndpoint = $"https://login.microsoftonline.com/{settings.TenantId}/oauth2/v2.0/token";
        string clientAssertion = BuildClientAssertion(settings, tokenEndpoint);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = settings.RedirectUri ?? string.Empty,
            ["client_id"] = settings.ClientId ?? string.Empty,
            ["code_verifier"] = codeVerifier,
            ["client_assertion_type"] = ClientAssertionType,
            ["client_assertion"] = clientAssertion,
        };

        using HttpResponseMessage response = await httpClient.PostAsync(
            tokenEndpoint, new FormUrlEncodedContent(form), ct);
        string body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Entra token exchange failed with status {(int)response.StatusCode}: {body}");
        }

        using JsonDocument doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("id_token", out JsonElement idTokenElement))
            throw new InvalidOperationException("Entra token response did not contain an id_token.");

        return idTokenElement.GetString()
            ?? throw new InvalidOperationException("Entra token response id_token was null.");
    }

    // Builds a private_key_jwt client assertion (RFC 7523 / OIDC core): a short-lived JWT
    // asserting the client's own identity, signed with the certificate configured for Entra
    // rather than a shared secret. iss/sub are both the client id per the spec; aud is the
    // token endpoint being called.
    private static string BuildClientAssertion(AzureAdSignInSettings settings, string tokenEndpoint)
    {
        X509Certificate2 cert = LoadCertificate(settings)
            ?? throw new InvalidOperationException(
                $"No usable client certificate loaded from '{settings.ClientCertificatePath}'. "
                + "Fix the certificate configuration; Connapse will not fall back to a client secret.");

        var signingCredentials = new X509SigningCredentials(cert, SecurityAlgorithms.RsaSha256);
        DateTime now = DateTime.UtcNow;

        var descriptor = new SecurityTokenDescriptor
        {
            Audience = tokenEndpoint,
            NotBefore = now.AddMinutes(-2),
            Expires = now.AddMinutes(5),
            SigningCredentials = signingCredentials,
            Claims = new Dictionary<string, object>
            {
                ["iss"] = settings.ClientId!,
                ["sub"] = settings.ClientId!,
                ["jti"] = Guid.NewGuid().ToString(),
            },
            // Entra identifies the signing cert by the base64url SHA-1 thumbprint in the `x5t`
            // header, not by `kid` — set it explicitly rather than relying on default header
            // population.
            AdditionalHeaderClaims = new Dictionary<string, object>
            {
                ["x5t"] = Base64UrlEncoder.Encode(cert.GetCertHash()),
            },
        };

        return Handler.CreateToken(descriptor);
    }

    // Mirrors Connapse.Storage.CloudScope.ConnapseAzureCredentials.LoadCertificate. Connapse.Identity
    // does not (and should not) reference Connapse.Storage, so this is a small local copy rather
    // than a shared helper — keep the two in sync if the loading rule ever changes.
    private static X509Certificate2? LoadCertificate(AzureAdSignInSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ClientCertificatePath)) return null;
        if (!File.Exists(settings.ClientCertificatePath)) return null;

        string ext = Path.GetExtension(settings.ClientCertificatePath).ToLowerInvariant();
        return ext is ".pem" or ".crt"
            ? X509Certificate2.CreateFromPemFile(settings.ClientCertificatePath)
            : X509CertificateLoader.LoadPkcs12FromFile(settings.ClientCertificatePath, settings.ClientCertificatePassword);
    }
}
