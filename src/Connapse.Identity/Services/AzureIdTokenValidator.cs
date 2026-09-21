using Connapse.Core;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Connapse.Identity.Services;

/// <summary>Outcome of validating an Entra id_token. Fails closed: unless <see cref="Ok"/> is
/// true, none of the other fields are populated, even if some claims could technically be read
/// off an otherwise-invalid token.</summary>
public sealed record AzureIdTokenResult(bool Ok, string? ObjectId, string? TenantId, string? DisplayName, string? Error)
{
    public static AzureIdTokenResult Failure(string error) => new(false, null, null, null, error);
}

/// <summary>
/// Validates an Entra ID id_token: signature (against the tenant's published JWKS), issuer,
/// audience, and expiry via <see cref="JsonWebTokenHandler"/>, then the OIDC <c>nonce</c> claim
/// against the value recorded when sign-in was started. Every failure mode returns
/// <see cref="AzureIdTokenResult.Ok"/> = false with nothing extracted — this is the boundary
/// where an attacker-controlled token first meets Connapse, so it must fail closed.
/// </summary>
public sealed class AzureIdTokenValidator(
    IOptionsMonitor<AzureAdSignInSettings> options,
    IAzureSigningKeySource signingKeySource)
{
    private static readonly JsonWebTokenHandler Handler = new();

    public async Task<AzureIdTokenResult> ValidateAsync(string idToken, string expectedNonce, CancellationToken ct)
    {
        AzureAdSignInSettings settings = options.CurrentValue;
        if (string.IsNullOrWhiteSpace(settings.TenantId) || string.IsNullOrWhiteSpace(settings.ClientId))
            return AzureIdTokenResult.Failure("Azure AD sign-in is not configured.");

        IReadOnlyList<SecurityKey> signingKeys;
        try
        {
            signingKeys = await signingKeySource.GetSigningKeysAsync(settings.TenantId, ct);
        }
        catch (Exception ex)
        {
            return AzureIdTokenResult.Failure($"Failed to fetch Entra signing keys: {ex.Message}");
        }

        if (signingKeys.Count == 0)
            return AzureIdTokenResult.Failure("No Entra signing keys available.");

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = $"https://login.microsoftonline.com/{settings.TenantId}/v2.0",
            ValidateAudience = true,
            ValidAudience = settings.ClientId,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = signingKeys,
            ValidateLifetime = true,
        };

        TokenValidationResult result;
        try
        {
            result = await Handler.ValidateTokenAsync(idToken, validationParameters);
        }
        catch (Exception ex)
        {
            // ValidateTokenAsync documents that it does not throw for validation failures (they
            // come back as !result.IsValid below), but malformed input can still throw before
            // validation runs at all — fail closed the same way either way.
            return AzureIdTokenResult.Failure(ex.Message);
        }

        if (!result.IsValid || result.SecurityToken is not JsonWebToken token)
            return AzureIdTokenResult.Failure(result.Exception?.Message ?? "id_token failed validation.");

        string? nonce = GetClaim(token, "nonce");
        if (!string.Equals(nonce, expectedNonce, StringComparison.Ordinal))
            return AzureIdTokenResult.Failure("id_token nonce does not match the sign-in request.");

        string? objectId = GetClaim(token, "oid");
        string? tenantId = GetClaim(token, "tid");
        if (string.IsNullOrWhiteSpace(objectId) || string.IsNullOrWhiteSpace(tenantId))
            return AzureIdTokenResult.Failure("id_token is missing the oid/tid claim.");

        string? displayName = GetClaim(token, "name") ?? GetClaim(token, "preferred_username");

        return new AzureIdTokenResult(true, objectId, tenantId, displayName, null);
    }

    private static string? GetClaim(JsonWebToken token, string type) =>
        token.Claims.FirstOrDefault(c => string.Equals(c.Type, type, StringComparison.Ordinal))?.Value;
}
