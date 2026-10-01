using Connapse.Identity.Services;
using Connapse.Storage.ConnectionTesters;
using Connapse.Web.Endpoints;
using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests.Services;

/// <summary>
/// A record's generated ToString prints every member, so one of these interpolated into a log line
/// or an exception message would carry its secret with it.
/// </summary>
[Trait("Category", "Unit")]
public class AtlassianSecretRedactionTests
{
    private const string Secret = "ATOAsuper-secret-value";

    [Fact]
    public void AtlassianSiteTestRequest_ToString_RedactsTheClientSecret()
    {
        string text = new AtlassianSiteTestRequest("https://acme.atlassian.net", "client-1", Secret).ToString();

        text.Should().NotContain(Secret).And.Contain("ClientSecret = ***").And.Contain("client-1");
    }

    [Fact]
    public void CreateAtlassianSiteRequest_ToString_RedactsTheClientSecret()
    {
        string text = new CreateAtlassianSiteRequest("https://acme.atlassian.net", "client-1", Secret).ToString();

        text.Should().NotContain(Secret).And.Contain("ClientSecret = ***").And.Contain("client-1");
    }

    [Fact]
    public void AtlassianPendingSignIn_ToString_RedactsTheCodeVerifier()
    {
        string text = new AtlassianPendingSignIn("state-1", Secret, Guid.Empty, DateTime.UnixEpoch, DateTime.UnixEpoch, RevocationGeneration: 0).ToString();

        text.Should().NotContain(Secret).And.Contain("CodeVerifier = ***").And.Contain("state-1");
    }
}
