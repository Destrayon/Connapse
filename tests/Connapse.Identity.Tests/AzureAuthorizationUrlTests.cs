using Connapse.Core;
using Connapse.Identity.Services;
using FluentAssertions;

namespace Connapse.Identity.Tests;

[Trait("Category", "Unit")]
public class AzureAuthorizationUrlTests
{
    [Fact]
    public void AuthUrl_ContainsRequiredParams()
    {
        var s = new AzureAdSignInSettings { TenantId = "t", ClientId = "c", RedirectUri = "https://app/azure/callback" };
        var url = AzureAuthorizationUrl.Build(s, "st", "no", "ch");
        url.Should().StartWith("https://login.microsoftonline.com/t/oauth2/v2.0/authorize?");
        url.Should().Contain("client_id=c").And.Contain("response_type=code")
           .And.Contain("code_challenge=ch").And.Contain("code_challenge_method=S256")
           .And.Contain("state=st").And.Contain("nonce=no")
           .And.Contain("redirect_uri=https%3A%2F%2Fapp%2Fazure%2Fcallback")
           .And.Contain("scope=openid%20profile");
    }
}
