using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Connapse.Core;
using Microsoft.IdentityModel.Tokens;

namespace Connapse.Identity.Services;

/// <summary>
/// Validates a Cognito ID token's signature, issuer, audience and lifetime, then checks the nonce
/// this deployment issued and that the pool has proven the email it carries.
/// </summary>
/// <remarks>
/// Split out from the callback endpoint specifically so each rejection path — bad signature, wrong
/// nonce, unverified email — can be exercised against a token forged locally with a throwaway
/// signing key, without needing a live Cognito pool to mint a genuine one.
/// <para>
/// Worth knowing why signature validation is belt-and-braces rather than the only line of defence:
/// the token arrives on a direct server-to-server call to the token endpoint, over TLS,
/// authenticated with the client secret, and OIDC Core §3.1.3.7 permits skipping signature
/// validation in exactly that case. It is done anyway because the claim being read here is the
/// join key into an authorization decision.
/// </para>
/// </remarks>
public static class CognitoIdTokenValidator
{
    /// <summary>
    /// Builds the <see cref="TokenValidationParameters"/> the callback endpoint validates a Cognito
    /// ID token against: the pool's own issuer and client id from <paramref name="settings"/>, and
    /// <paramref name="signingKeys"/> fetched by the caller from the pool's discovery document.
    /// </summary>
    /// <remarks>
    /// Pulled out from the endpoint so the five validation flags are pinned by a named unit test
    /// each — a single flag flipped back to <see langword="false"/> (or the endpoint forgetting to
    /// call this at all) fails one specific test rather than silently weakening validation for
    /// every token this deployment ever accepts.
    /// </remarks>
    public static TokenValidationParameters BuildValidationParameters(
        CognitoSettings settings, IEnumerable<SecurityKey> signingKeys) =>
        new()
        {
            ValidIssuer = settings.IssuerUrl,
            ValidAudience = settings.ClientId,
            IssuerSigningKeys = signingKeys,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        };

    /// <summary>
    /// Validates <paramref name="idToken"/> against <paramref name="validationParameters"/> (the
    /// pool's signing keys, issuer and audience — built by the caller from live discovery, or from
    /// a throwaway key in a test), then checks its <c>nonce</c> claim against
    /// <paramref name="expectedNonce"/> and that <c>email_verified</c> is true.
    /// </summary>
    public static CognitoIdTokenResult Validate(
        string idToken,
        TokenValidationParameters validationParameters,
        string? expectedNonce)
    {
        // Cognito issues bare claim names ("email", "sub", ...). JwtSecurityTokenHandler's default
        // inbound claim map rewrites well-known names like "email"/"sub" to legacy XML-schema
        // claim URIs, which would make every lookup below fail silently. Disabling it on this
        // instance only keeps the claims exactly as the token carries them.
        JwtSecurityTokenHandler handler = new() { InboundClaimTypeMap = new Dictionary<string, string>() };
        ClaimsPrincipal principal;
        try
        {
            principal = handler.ValidateToken(idToken, validationParameters, out _);
        }
        catch (SecurityTokenException)
        {
            return CognitoIdTokenResult.Rejected("token_invalid");
        }
        catch (ArgumentException)
        {
            return CognitoIdTokenResult.Rejected("token_invalid");
        }

        // Rule: bind the nonce this deployment issued. Stops a token minted for a different
        // authorization request being replayed into this one.
        string? tokenNonce = principal.FindFirst("nonce")?.Value;
        if (string.IsNullOrEmpty(expectedNonce) || string.IsNullOrEmpty(tokenNonce) ||
            !string.Equals(tokenNonce, expectedNonce, StringComparison.Ordinal))
        {
            return CognitoIdTokenResult.Rejected("nonce_mismatch");
        }

        // Rule: refuse an unverified email. The pool has not proven the address, and the address
        // is the join key into Identity Center.
        string? emailVerified = principal.FindFirst("email_verified")?.Value;
        string? email = principal.FindFirst("email")?.Value;
        if (!string.Equals(emailVerified, "true", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(email))
        {
            return CognitoIdTokenResult.Rejected("email_not_verified");
        }

        return CognitoIdTokenResult.Accepted(email);
    }
}

/// <summary>The outcome of validating a Cognito ID token: either the verified email, or why not.</summary>
public sealed record CognitoIdTokenResult(bool Success, string? Email, string? FailureReason)
{
    public static CognitoIdTokenResult Accepted(string email) => new(true, email, null);
    public static CognitoIdTokenResult Rejected(string reason) => new(false, null, reason);
}
