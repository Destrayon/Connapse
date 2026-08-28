using Connapse.Core.Utilities;
using FluentAssertions;

namespace Connapse.Core.Tests.Utilities;

[Trait("Category", "Unit")]
public class CognitoRedirectTests
{
    [Theory]
    [InlineData("https://connapse.example.com")]
    [InlineData("https://connapse.example.com/")]
    [InlineData("https://connapse.example.com:8443/")]
    // HTTPS is accepted on a private address too. Cognito's rule is about the scheme, and a
    // deployment behind a company VPN with a real certificate is a normal way to run this.
    [InlineData("https://192.168.1.50:5001/")]
    public void IsUsableOrigin_OverHttps_IsAccepted(string origin) =>
        CognitoRedirect.IsUsableOrigin(origin).Should().BeTrue();

    [Theory]
    [InlineData("http://localhost:5001/")]
    [InlineData("http://LOCALHOST:5001/")]
    [InlineData("http://127.0.0.1:5001/")]
    [InlineData("http://[::1]:5001/")]
    public void IsUsableOrigin_PlainHttpOnLoopback_IsAccepted(string origin) =>
        CognitoRedirect.IsUsableOrigin(origin).Should().BeTrue();

    [Theory]
    // The case this exists for: a LAN deployment that would fail at AWS after a full setup.
    [InlineData("http://192.168.1.50:5001/")]
    [InlineData("http://connapse.example.com/")]
    public void IsUsableOrigin_PlainHttpOffLoopback_IsRefused(string origin) =>
        CognitoRedirect.IsUsableOrigin(origin).Should().BeFalse();

    [Fact]
    public void IsUsableOrigin_LoopbackBlockBeyondTheDocumentedAddress_IsRefused()
    {
        // Uri.IsLoopback would accept this — it treats all of 127.0.0.0/8 as loopback — and AWS
        // would then refuse it. Refusing here keeps the explanation on the side that can give one.
        CognitoRedirect.IsUsableOrigin("http://127.0.0.2:5001/").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("connapse.example.com")]
    [InlineData("ftp://connapse.example.com/")]
    public void IsUsableOrigin_WithNothingUsable_IsRefused(string? origin) =>
        CognitoRedirect.IsUsableOrigin(origin).Should().BeFalse();

    [Theory]
    [InlineData("https://connapse.example.com", "https://connapse.example.com/api/v1/auth/cloud/cognito/callback")]
    [InlineData("https://connapse.example.com/", "https://connapse.example.com/api/v1/auth/cloud/cognito/callback")]
    [InlineData("http://localhost:5001/", "http://localhost:5001/api/v1/auth/cloud/cognito/callback")]
    public void CallbackFor_BuildsTheRegisteredUrl(string origin, string expected) =>
        CognitoRedirect.CallbackFor(origin).Should().Be(expected);

    [Fact]
    public void CallbackFor_WhenCognitoWouldRefuseTheOrigin_IsNull()
    {
        // Null rather than a best-effort string: the only use for this value is pasting it into
        // AWS, and one Cognito will not accept is worse than none.
        CognitoRedirect.CallbackFor("http://192.168.1.50:5001/").Should().BeNull();
    }

    [Fact]
    public void CallbackFor_IgnoresAPathOnTheOrigin()
    {
        // BaseUri carries whatever the app is hosted under. The callback is an absolute route, so
        // it must resolve against the authority rather than append to the path.
        CognitoRedirect.CallbackFor("https://connapse.example.com/connapse/")
            .Should().Be("https://connapse.example.com/api/v1/auth/cloud/cognito/callback");
    }
}
