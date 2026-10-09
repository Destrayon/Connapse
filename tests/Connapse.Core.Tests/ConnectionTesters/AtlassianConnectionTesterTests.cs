using System.Net;
using Connapse.Core.Tests.Connectors;
using Connapse.Storage.ConnectionTesters;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.ConnectionTesters;

[Trait("Category", "Unit")]
public sealed class AtlassianConnectionTesterTests : IDisposable
{
    private const string CloudId = "11111111-2222-3333-4444-555555555555";
    private const string Root = "/ex/confluence/" + CloudId + "/wiki";
    private const string OwnAccount = "svc-account";

    private readonly FakeAtlassianApi _api = new();
    private readonly AtlassianConnectionTester _tester;
    private readonly AtlassianSiteTestRequest _request = new("https://acme.atlassian.net", "client-1", "secret");

    public AtlassianConnectionTesterTests()
    {
        _tester = new AtlassianConnectionTester(new FakeFactory(_api));
        _api.MapJson(FakeAtlassianApi.TenantInfoPath, new { cloudId = CloudId });
        _api.MapJson(Root + "/rest/api/user/current", new { accountId = OwnAccount });
        _api.MapJson(Root + "/api/v2/spaces", new { results = new[] { new { id = "1" } } });
        _api.MapJson(Root + "/api/v2/pages", new { results = new[] { new { id = "4242" } } });
        _api.MapJson(Root + "/rest/api/search/user", new
        {
            results = new object[]
            {
                new { user = new { accountId = OwnAccount } },
                new { user = new { accountId = "someone-else" } },
            },
        });
        _api.MapJson(Root + "/rest/api/content/4242/permission/check", new { hasPermission = true });
    }

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task Test_AllProbesPass_Succeeds()
    {
        string? checkBody = null;
        _api.Map(Root + "/rest/api/content/4242/permission/check", request =>
        {
            checkBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return FakeAtlassianApi.Json(new { hasPermission = true });
        });

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeTrue();
        result.Details!["cloudId"].Should().Be(CloudId);
        result.Details["siteUrl"].Should().Be("https://acme.atlassian.net");
        checkBody.Should().Contain("someone-else").And.NotContain(OwnAccount);
    }

    [Fact]
    public async Task Test_BadSecret_FailsAtTokenStep()
    {
        _api.FailTokenWith(HttpStatusCode.Unauthorized);

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Details!["step"].Should().Be(AtlassianConnectionTester.TokenStep);
        result.Message.Should().Contain("rejected the client ID or secret");
    }

    [Fact]
    public async Task Test_CannotReadConfluence_FailsAtIdentityStep()
    {
        _api.Map(Root + "/rest/api/user/current", _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Details!["step"].Should().Be(AtlassianConnectionTester.IdentityStep);
    }

    [Fact]
    public async Task Test_NoSpaceAccess_FailsAtSpacesStep()
    {
        _api.Map(Root + "/api/v2/spaces", _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Details!["step"].Should().Be(AtlassianConnectionTester.SpacesStep);
    }

    [Fact]
    public async Task Test_UserCurrentWithoutAccountId_FailsAtIdentityStepWithoutACheck()
    {
        _api.MapJson(Root + "/rest/api/user/current", new { });

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Details!["step"].Should().Be(AtlassianConnectionTester.IdentityStep);
        _api.Calls.Keys.Should().NotContain(p => p.EndsWith("/permission/check"));
    }

    [Fact]
    public async Task Test_PermissionCheckAnswers401_FailsAtAdminStep()
    {
        _api.Map(Root + "/rest/api/content/4242/permission/check", _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Details!["step"].Should().Be(AtlassianConnectionTester.AdminStep);
        result.Message.Should().Contain("Confluence Administrator");
    }

    [Fact]
    public async Task Test_MalformedSpacesBody_FailsAtSpacesStep()
    {
        _api.Map(Root + "/api/v2/spaces", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"),
        });

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Details!["step"].Should().Be(AtlassianConnectionTester.SpacesStep);
    }

    [Fact]
    public async Task Test_NullBody_FailsAtItsStepInsteadOfThrowing()
    {
        _api.Map(Root + "/api/v2/spaces", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json"),
        });

        var result = await _tester.TestConnectionAsync(_request);

        result.Details!["step"].Should().Be(AtlassianConnectionTester.SpacesStep);
    }

    [Fact]
    public async Task Test_NoSpaces_FailsAtSpacesStep()
    {
        _api.MapJson(Root + "/api/v2/spaces", new { results = Array.Empty<object>() });

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Details!["step"].Should().Be(AtlassianConnectionTester.SpacesStep);
    }

    [Fact]
    public async Task Test_PagesForbidden_NamesTheMissingScopeNotSpaces()
    {
        _api.Map(Root + "/api/v2/pages", _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("read:page:confluence");
        result.Message.Should().NotContain("no Confluence access");
    }

    [Fact]
    public async Task Test_UserSearchServerError_IsNotReportedAsNotAdmin()
    {
        _api.Map(Root + "/rest/api/search/user", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Details!["step"].Should().Be(AtlassianConnectionTester.AdminStep);
        result.Message.Should().Contain("Couldn't verify").And.NotContain("isn't Confluence Administrator");
    }

    [Fact]
    public async Task Test_TenantInfoTimesOut_FailsAtSiteStepInsteadOfThrowing()
    {
        _api.Map(FakeAtlassianApi.TenantInfoPath, _ => throw new TaskCanceledException("HttpClient timeout"));

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Details!["step"].Should().Be(AtlassianConnectionTester.SiteStep);
    }

    [Fact]
    public async Task Test_TenantInfoIsAskedOfTheSitesOwnHost()
    {
        await _tester.TestConnectionAsync(_request);

        _api.Requests.Should().Contain(u => u.AbsolutePath == FakeAtlassianApi.TenantInfoPath && u.Host == "acme.atlassian.net");
    }

    [Fact]
    public async Task Test_NotAdmin_FailsAtAdminStep()
    {
        _api.Map(Root + "/rest/api/content/4242/permission/check", _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Details!["step"].Should().Be(AtlassianConnectionTester.AdminStep);
        result.Message.Should().Contain("Confluence Administrator");
    }

    [Theory]
    [InlineData("", "application/json")]
    [InlineData("<html><body>Log in to Atlassian</body></html>", "text/html")]
    [InlineData("{not json", "application/json")]
    [InlineData("{}", "application/json")]
    [InlineData("{\"hasPermission\":\"true\"}", "application/json")]
    [InlineData("[{\"hasPermission\":true}]", "application/json")]
    [InlineData("null", "application/json")]
    public async Task Test_PermissionCheck200WithoutAPermissionAnswer_FailsAtAdminStep(string body, string mediaType)
    {
        _api.Map(Root + "/rest/api/content/4242/permission/check", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType),
        });

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Details!["step"].Should().Be(AtlassianConnectionTester.AdminStep);
        result.Message.Should().Contain("Couldn't verify");
    }

    [Fact]
    public async Task Test_PermissionCheckSaysOtherUserCannotRead_StillProvesAdmin()
    {
        _api.MapJson(Root + "/rest/api/content/4242/permission/check", new { hasPermission = false });

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Test_NoPages_WarnsAndRefusesSave()
    {
        _api.MapJson(Root + "/api/v2/pages", new { results = Array.Empty<object>() });

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Details!["step"].Should().Be(AtlassianConnectionTester.AdminStep);
        result.Details["warning"].Should().Be(true);
        _api.Calls.Keys.Should().NotContain(p => p.EndsWith("/permission/check"));
    }

    [Fact]
    public async Task Test_OnlyTheServiceAccountExists_FailsAtAdminStepWithoutProbing()
    {
        _api.MapJson(Root + "/rest/api/search/user", new
        {
            results = new object[] { new { user = new { accountId = OwnAccount } } },
        });

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Details!["step"].Should().Be(AtlassianConnectionTester.AdminStep);
        _api.Calls.Keys.Should().NotContain(p => p.EndsWith("/permission/check"));
    }

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("https://acme.atlassian.net.evil.com")]
    [InlineData("http://acme.atlassian.net")]
    [InlineData("https://acme.atlassian.net:8443")]
    [InlineData("https://acme.atlassian.net/wiki")]
    [InlineData("")]
    public async Task Test_SiteThatIsNotAnAtlassianCloudAddress_FailsWithoutAnyRequest(string siteUrl)
    {
        var result = await _tester.TestConnectionAsync(_request with { SiteUrl = siteUrl });

        result.Success.Should().BeFalse();
        result.Details!["step"].Should().Be(AtlassianConnectionTester.SiteStep);
        _api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_SiteWithoutTenantInfo_FailsAtSiteStep()
    {
        _api.MapJson(FakeAtlassianApi.TenantInfoPath, new { cloudId = "not-a-guid" });

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Details!["step"].Should().Be(AtlassianConnectionTester.SiteStep);
        _api.TokenRequests.Should().Be(0);
    }

    [Fact]
    public async Task Test_RateLimited_FailsWithoutThrowing()
    {
        _api.RateLimitNext = true;

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("rate limiting");
    }

    private sealed class FakeFactory(FakeAtlassianApi api) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => api.CreateClient();
    }
}
