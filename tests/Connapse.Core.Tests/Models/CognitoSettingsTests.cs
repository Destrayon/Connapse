using Connapse.Core;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Models;

/// <summary>
/// Whether a deployment has enough to talk to a Cognito user pool.
/// </summary>
[Trait("Category", "Unit")]
public class CognitoSettingsTests
{
    private static CognitoSettings Complete() => new()
    {
        IssuerUrl = "https://cognito-idp.us-west-1.amazonaws.com/us-west-1_abc123",
        Domain = "https://connapse.auth.us-west-1.amazoncognito.com",
        ClientId = "3ia37m5mg4rtioih2slv8etmed",
        ClientSecret = "shh",
        Region = "us-west-1",
    };

    [Fact]
    public void IsConfigured_WithEveryField_IsTrue()
    {
        Complete().IsConfigured.Should().BeTrue();
    }

    [Theory]
    [InlineData("http://cognito-idp.us-west-1.amazonaws.com/us-west-1_abc123")]
    [InlineData("http://connapse.auth.us-west-1.amazoncognito.com")]
    public void IsConfigured_WithAPlainHttpUrl_IsFalse(string insecure)
    {
        // Both URLs carry a credential — an authorization code on one, a token on the other — so
        // a plain-HTTP hop puts it on the wire in cleartext. Cognito refuses these too, so the
        // only thing accepting them buys is a rejection from AWS instead of an explanation here.
        var settings = Complete();
        settings.Domain = insecure;

        settings.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_WithLoopbackHttp_IsFalse()
    {
        // No loopback exception here: IssuerUrl and Domain are always AWS-hosted endpoints
        // (there is no such thing as a localhost Cognito pool), unlike the OAuth redirect URI,
        // which is Connapse's own address and is computed from the incoming request rather than
        // read from these settings. A loopback allowance on these two would only widen what is
        // accepted without buying anything.
        var settings = Complete();
        settings.Domain = "http://localhost:5001";

        settings.IsConfigured.Should().BeFalse();
    }

    [Theory]
    [InlineData("IssuerUrl")]
    [InlineData("Domain")]
    [InlineData("ClientId")]
    [InlineData("ClientSecret")]
    [InlineData("Region")]
    public void IsConfigured_WithAnyFieldMissing_IsFalse(string missing)
    {
        // Every field is load-bearing, and a half-configured pool fails at a different step
        // depending on which half is missing — the redirect 404s, or the token exchange 401s,
        // or the issuer does not match what Identity Center trusts. One check up front is
        // cheaper to explain than five failures spread across the flow.
        var settings = Complete();
        typeof(CognitoSettings).GetProperty(missing)!.SetValue(settings, "");

        settings.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_WithWhitespaceOnly_IsFalse()
    {
        // A settings row saved from a form with a stray space is not configuration.
        var settings = Complete();
        settings.ClientId = "   ";

        settings.IsConfigured.Should().BeFalse();
    }
}
