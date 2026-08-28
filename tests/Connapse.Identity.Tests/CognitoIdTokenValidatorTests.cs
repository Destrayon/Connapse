using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;

namespace Connapse.Identity.Tests;

/// <summary>
/// The Cognito ID token checks that stand between a forged or misdirected token and a stored
/// identity link. Every token here is signed locally with a throwaway RSA key — no live Cognito
/// pool involved — because the point is to prove each rejection path actually fires, not to
/// exercise Cognito itself. <see cref="CognitoIdTokenValidator.Validate"/> is the only gate before
/// <c>AwsIdentityLinkStore.SaveAsync</c> is called in the callback endpoint, so a rejection here is
/// exactly the case where that save is never reached.
/// </summary>
[Trait("Category", "Unit")]
public class CognitoIdTokenValidatorTests
{
    private const string Issuer = "https://cognito-idp.us-west-1.amazonaws.com/us-west-1_test";
    private const string Audience = "test-client-id";
    private const string Nonce = "expected-nonce-value";

    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test-key" };
    private static readonly RsaSecurityKey OtherKey = new(RSA.Create(2048)) { KeyId = "other-key" };

    private static TokenValidationParameters ValidationParameters(SecurityKey? trustedKey = null) => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = trustedKey ?? SigningKey,
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        RequireSignedTokens = true,
        ClockSkew = TimeSpan.FromMinutes(2),
    };

    private static string ForgeToken(
        string issuer = Issuer,
        string audience = Audience,
        SecurityKey? signWith = null,
        DateTime? expires = null,
        string? nonce = Nonce,
        string email = "person@example.com",
        bool? emailVerified = true)
    {
        var credentials = new SigningCredentials(signWith ?? SigningKey, SecurityAlgorithms.RsaSha256);

        var claims = new List<Claim>
        {
            new("sub", Guid.NewGuid().ToString()),
            new("email", email),
        };
        if (emailVerified.HasValue)
            claims.Add(new Claim("email_verified", emailVerified.Value ? "true" : "false"));
        if (nonce is not null)
            claims.Add(new Claim("nonce", nonce));

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-15),
            expires: expires ?? DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public void Validate_WellFormedToken_IsAccepted()
    {
        var token = ForgeToken();

        var result = CognitoIdTokenValidator.Validate(token, ValidationParameters(), Nonce);

        result.Success.Should().BeTrue();
        result.Email.Should().Be("person@example.com");
        result.FailureReason.Should().BeNull();
    }

    [Fact]
    public void Validate_SignedWithAKeyTheValidatorDoesNotTrust_IsRejected()
    {
        // A token that did not come from the pool it claims to — the exact shape of a forgery.
        var token = ForgeToken(signWith: OtherKey);

        var result = CognitoIdTokenValidator.Validate(token, ValidationParameters(), Nonce);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be("token_invalid");
        result.Email.Should().BeNull();
    }

    [Fact]
    public void Validate_WrongIssuer_IsRejected()
    {
        var token = ForgeToken(issuer: "https://cognito-idp.us-west-1.amazonaws.com/us-west-1_someone_else");

        var result = CognitoIdTokenValidator.Validate(token, ValidationParameters(), Nonce);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be("token_invalid");
    }

    [Fact]
    public void Validate_WrongAudience_IsRejected()
    {
        var token = ForgeToken(audience: "some-other-client-id");

        var result = CognitoIdTokenValidator.Validate(token, ValidationParameters(), Nonce);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be("token_invalid");
    }

    [Fact]
    public void Validate_ExpiredToken_IsRejected()
    {
        var token = ForgeToken(expires: DateTime.UtcNow.AddMinutes(-10));

        var result = CognitoIdTokenValidator.Validate(token, ValidationParameters(), Nonce);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be("token_invalid");
    }

    [Fact]
    public void Validate_NonceDoesNotMatchWhatThisDeploymentIssued_IsRejected()
    {
        var token = ForgeToken(nonce: "a-nonce-the-token-carries");

        var result = CognitoIdTokenValidator.Validate(
            token, ValidationParameters(), "a-different-nonce-this-deployment-issued");

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be("nonce_mismatch");
    }

    [Fact]
    public void Validate_TokenCarriesNoNonce_IsRejected()
    {
        var token = ForgeToken(nonce: null);

        var result = CognitoIdTokenValidator.Validate(token, ValidationParameters(), Nonce);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be("nonce_mismatch");
    }

    [Fact]
    public void Validate_EmailNotVerified_IsRejected()
    {
        var token = ForgeToken(emailVerified: false);

        var result = CognitoIdTokenValidator.Validate(token, ValidationParameters(), Nonce);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be("email_not_verified");
        result.Email.Should().BeNull("nothing should be readable out of a rejected result");
    }

    [Fact]
    public void Validate_EmailVerifiedClaimMissing_IsRejected()
    {
        var token = ForgeToken(emailVerified: null);

        var result = CognitoIdTokenValidator.Validate(token, ValidationParameters(), Nonce);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be("email_not_verified");
    }
}
