using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Connapse.Core;
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
        bool? emailVerified = true,
        string? preferredUsername = "Patrick.Summers")
    {
        var credentials = new SigningCredentials(signWith ?? SigningKey, SecurityAlgorithms.RsaSha256);

        var claims = new List<Claim>
        {
            new("sub", Guid.NewGuid().ToString()),
            new("email", email),
        };
        if (preferredUsername is not null)
            claims.Add(new Claim("preferred_username", preferredUsername));
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
        result.DirectoryUserName.Should().Be("Patrick.Summers");
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

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void Validate_UnverifiedEmail_IsAccepted(bool? emailVerified)
    {
        // The whole reason the join key moved. Cognito marks a SAML-federated user's mapped email
        // unverified by default and cannot verify it with a one-time code, so refusing on this
        // would reject every user of the configuration this feature is built around. The email is
        // display data now; nothing authorizes from it.
        var token = ForgeToken(emailVerified: emailVerified);

        var result = CognitoIdTokenValidator.Validate(token, ValidationParameters(), Nonce);

        result.Success.Should().BeTrue();
        result.DirectoryUserName.Should().Be("Patrick.Summers");
    }

    [Fact]
    public void Validate_WithNoDirectoryUserName_IsRejected()
    {
        // Valid signature, right issuer, right audience, live — and it names nobody the directory
        // can resolve. Accepting it would store a link that fails at permission-resolution time,
        // which is the failure this validator exists to move earlier.
        var token = ForgeToken(preferredUsername: null);

        var result = CognitoIdTokenValidator.Validate(token, ValidationParameters(), Nonce);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be("no_directory_user");
        result.DirectoryUserName.Should().BeNull("nothing should be readable out of a rejected result");
        result.Email.Should().BeNull();
    }

    [Fact]
    public void Validate_DirectoryUserName_KeepsItsCase()
    {
        // The email this replaced was lower-cased before storage, which is safe for addresses and
        // wrong for user names: this one belongs to a directory Connapse does not own, and folding
        // its case would record an identifier that may never have existed.
        var token = ForgeToken(preferredUsername: "Patrick.Summers");

        var result = CognitoIdTokenValidator.Validate(token, ValidationParameters(), Nonce);

        result.DirectoryUserName.Should().Be("Patrick.Summers");
    }

    // --- BuildValidationParameters ---
    //
    // These exist because CognitoIdTokenValidatorTests above builds its own
    // TokenValidationParameters by hand, which proves Validate() honours whatever it is handed —
    // nothing proved the endpoint hands it the right thing. Each flag gets its own test so a single
    // flag flipped back to false (or the endpoint forgetting to call this factory at all) fails a
    // named test rather than a shared one.

    private static CognitoSettings BuildSettings() => new()
    {
        IssuerUrl = Issuer,
        Domain = "https://example.auth.us-west-1.amazoncognito.com",
        ClientId = Audience,
        ClientSecret = "secret",
        Region = "us-west-1",
    };

    [Fact]
    public void BuildValidationParameters_ValidatesIssuer()
    {
        var parameters = CognitoIdTokenValidator.BuildValidationParameters(BuildSettings(), [SigningKey]);

        parameters.ValidateIssuer.Should().BeTrue();
    }

    [Fact]
    public void BuildValidationParameters_ValidatesAudience()
    {
        var parameters = CognitoIdTokenValidator.BuildValidationParameters(BuildSettings(), [SigningKey]);

        parameters.ValidateAudience.Should().BeTrue();
    }

    [Fact]
    public void BuildValidationParameters_ValidatesLifetime()
    {
        var parameters = CognitoIdTokenValidator.BuildValidationParameters(BuildSettings(), [SigningKey]);

        parameters.ValidateLifetime.Should().BeTrue();
    }

    [Fact]
    public void BuildValidationParameters_ValidatesIssuerSigningKey()
    {
        var parameters = CognitoIdTokenValidator.BuildValidationParameters(BuildSettings(), [SigningKey]);

        parameters.ValidateIssuerSigningKey.Should().BeTrue();
    }

    [Fact]
    public void BuildValidationParameters_RequiresSignedTokens()
    {
        var parameters = CognitoIdTokenValidator.BuildValidationParameters(BuildSettings(), [SigningKey]);

        parameters.RequireSignedTokens.Should().BeTrue();
    }

    [Fact]
    public void BuildValidationParameters_UsesIssuerFromSettings()
    {
        var parameters = CognitoIdTokenValidator.BuildValidationParameters(BuildSettings(), [SigningKey]);

        parameters.ValidIssuer.Should().Be(Issuer);
    }

    [Fact]
    public void BuildValidationParameters_UsesAudienceFromSettings()
    {
        var parameters = CognitoIdTokenValidator.BuildValidationParameters(BuildSettings(), [SigningKey]);

        parameters.ValidAudience.Should().Be(Audience);
    }

    [Fact]
    public void BuildValidationParameters_PassesThroughTheSigningKeysItWasGiven()
    {
        var parameters = CognitoIdTokenValidator.BuildValidationParameters(BuildSettings(), [SigningKey]);

        parameters.IssuerSigningKeys.Should().ContainSingle().Which.Should().BeSameAs(SigningKey);
    }

    [Fact]
    public void BuildValidationParameters_ThenValidate_AcceptsATokenSignedWithThePassedThroughKey()
    {
        // The end-to-end proof: parameters built by the factory, handed to Validate(), against a
        // token forged with the same key the factory was given — exactly the endpoint's own path,
        // minus the live network calls that fetch settings and signing keys.
        var token = ForgeToken();

        var parameters = CognitoIdTokenValidator.BuildValidationParameters(BuildSettings(), [SigningKey]);
        var result = CognitoIdTokenValidator.Validate(token, parameters, Nonce);

        result.Success.Should().BeTrue();
    }
}
