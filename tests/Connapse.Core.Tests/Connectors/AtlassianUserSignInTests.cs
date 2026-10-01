using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.Atlassian;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Connectors;

[Trait("Category", "Unit")]
public class AtlassianUserSignInTests
{
    private const string Redirect = "https://connapse.example/api/v1/auth/cloud/atlassian/callback";

    [Fact]
    public void AuthorizeUrl_WithoutPkce_IsTheReadMeConsentUrl()
    {
        AtlassianUserSignIn.AuthorizeUrl("client-1", Redirect, "state-1", codeChallenge: null).Should().Be(
            "https://auth.atlassian.com/authorize?audience=api.atlassian.com&client_id=client-1&scope=read%3Ame"
            + "&redirect_uri=https%3A%2F%2Fconnapse.example%2Fapi%2Fv1%2Fauth%2Fcloud%2Fatlassian%2Fcallback"
            + "&state=state-1&response_type=code&prompt=consent");
    }

    [Fact]
    public void AuthorizeUrl_WithPkce_AddsTheS256Challenge()
    {
        string url = AtlassianUserSignIn.AuthorizeUrl("client-1", Redirect, "state-1", "challenge-1");

        url.Should().EndWith("&code_challenge=challenge-1&code_challenge_method=S256");
        url.Should().NotContain("offline_access");
    }

    [Fact]
    public async Task ResolveAsync_MeWithoutAccountId_Throws()
    {
        using var api = new FakeAtlassianApi();
        api.AcceptAuthorizationCode("code-1");
        api.MapJson("/me", new { name = "Nobody" });

        var act = () => SignIn(api).ResolveAsync("code-1", "verifier-1", Redirect);

        await act.Should().ThrowAsync<AtlassianAuthException>();
    }

    [Fact]
    public async Task ResolveAsync_SendsTheVerifierAndReturnsTheAccount()
    {
        using var api = new FakeAtlassianApi();
        api.AcceptAuthorizationCode("code-1");
        api.MapJson("/me", new { account_id = "acc-1", name = "Ada Lovelace", email = "ada@example.com" });

        var account = await SignIn(api).ResolveAsync("code-1", "verifier-1", Redirect);

        account.Should().Be(new AtlassianUserAccount("acc-1", "Ada Lovelace", "ada@example.com"));
        api.AuthorizationCodeBodies.Should().ContainSingle()
            .Which.Should().Contain("\"code_verifier\":\"verifier-1\"").And.Contain("\"client_secret\":\"secret-1\"");
        api.Requests.Should().Contain(new Uri(AtlassianUserSignIn.MeEndpoint));
    }

    private static AtlassianUserSignIn SignIn(FakeAtlassianApi api)
    {
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(AtlassianApiClient.HttpClientName).Returns(_ => api.CreateClient());
        var credentials = Substitute.For<IProviderCredentialStore>();
        credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>()).Returns(new AtlassianLinkAppRegistration("client-1"));
        credentials.GetAtlassianLinkAppSecretAsync(Arg.Any<CancellationToken>()).Returns("secret-1");
        return new AtlassianUserSignIn(http, credentials);
    }
}
