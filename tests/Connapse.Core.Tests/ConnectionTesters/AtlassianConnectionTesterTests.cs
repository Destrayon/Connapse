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
        // The content step's probes, one per scope a sync needs.
        _api.MapJson(Root + CommentsPath, new { results = Array.Empty<object>() });
        _api.MapJson(Root + FolderPath, new { id = "4242", title = "x" });
        _api.MapJson(Root + "/rest/api/user/bulk", new { results = Array.Empty<object>() });
        _api.MapJson(Root + "/rest/api/search", new { results = Array.Empty<object>() });
        _api.MapJson(Root + AttachmentsPath, new { results = Array.Empty<object>() });
    }

    private const string CommentsPath = "/api/v2/pages/4242/footer-comments";
    private const string FolderPath = "/api/v2/folders/4242";
    private const string AttachmentsPath = "/api/v2/pages/4242/attachments";

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
    public async Task Test_ConfluencePasses_ProductsIsConfluence()
    {
        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeTrue();
        result.Details!["products"].Should().Be("confluence");
        result.Details.Should().NotContainKey("product:confluence");
    }

    [Fact]
    public async Task Test_ConfluenceWarns_ProductsIsConfluence()
    {
        _api.MapJson(Root + "/api/v2/pages", new { results = Array.Empty<object>() });

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Details!["warning"].Should().Be(true);
        result.Details["products"].Should().Be("confluence");
    }

    [Fact]
    public async Task Test_ConfluenceFails_SameStepAndMessageAsBefore()
    {
        _api.Map(Root + "/api/v2/spaces", _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Message.Should().Be(ConfluenceSiteProbe.NoSpacesMessage);
        result.Details!["step"].Should().Be(AtlassianConnectionTester.SpacesStep);
        result.Details["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.NoSpaces);
        result.Details["detail"].Should().Be("HTTP 403 (Forbidden)");
        result.Details["product:confluence"].Should().Be(ConfluenceSiteProbe.NoSpacesMessage);
        result.Details.Should().NotContainKey("products");
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

    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"access_token\":\"t\",\"expires_in\":\"soon\"}")]
    public async Task Test_MalformedTokenResponse_FailsAtTokenStepInsteadOfThrowing(string body)
    {
        _api.AnswerTokenWith(body);

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Details!["step"].Should().Be(AtlassianConnectionTester.TokenStep);
    }

    [Fact]
    public async Task Test_TokenEndpointServerError_FailsAtTokenStep()
    {
        _api.FailTokenWith(HttpStatusCode.InternalServerError);

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Details!["step"].Should().Be(AtlassianConnectionTester.TokenStep);
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
    public async Task Test_PermissionCheckAnswers401_FailsAtAdminStepAsAMissingScope()
    {
        // The same token read Confluence a moment ago, so a 401 here is the token's scopes, not the account.
        _api.Map(Root + "/rest/api/content/4242/permission/check", _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Details!["step"].Should().Be(AtlassianConnectionTester.AdminStep);
        result.Details["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.MissingScope);
        result.Message.Should().Contain("read:content.permission:confluence").And.NotContain("isn't Confluence Administrator");
    }

    [Fact]
    public async Task Test_PermissionCheck403NamingAScope_IsAMissingScopeWithAtlassiansAnswer()
    {
        _api.Map(Root + "/rest/api/content/4242/permission/check", _ => FakeAtlassianApi.Json(
            new { code = 403, message = "Unauthorized; scope does not match" }, HttpStatusCode.Forbidden));

        var result = await _tester.TestConnectionAsync(_request);

        result.Details!["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.MissingScope);
        result.Details["detail"].Should().Be("HTTP 403 (Forbidden): 403 Unauthorized; scope does not match");
    }

    [Fact]
    public async Task Test_PermissionCheck403_WhenTheTokenListsNoPermissionScope_IsAMissingScope()
    {
        _api.TokenScope = "read:space:confluence read:page:confluence";
        _api.Map(Root + "/rest/api/content/4242/permission/check", _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await _tester.TestConnectionAsync(_request);

        result.Details!["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.MissingScope);
    }

    [Fact]
    public async Task Test_NotAdmin_LinksNotAdministrator()
    {
        _api.TokenScope = string.Join(' ', AtlassianSiteScopes.All.Select(s => s.Name));
        _api.Map(Root + "/rest/api/content/4242/permission/check", _ => FakeAtlassianApi.Json(
            new { message = "User is not permitted to check permissions" }, HttpStatusCode.Forbidden));

        var result = await _tester.TestConnectionAsync(_request);

        result.Details!["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.NotAdministrator);
        result.Message.Should().Contain("isn't Confluence Administrator").And.Contain("then test again");
    }

    [Fact]
    public async Task Test_Pass_SaysWhatWasVerified_AndConfirmsTheGrantedScopes()
    {
        _api.TokenScope = "read:space:confluence read:page:confluence read:content.permission:confluence offline_access";

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("found the site").And.Contain("signed in").And.Contain("listed spaces")
            .And.Contain("read comments, folders and changes");
        // Every required scope was probed; the attachment scope's probe passed too.
        ((string[])result.Details!["scopes"]).Should().Equal(AtlassianSiteScopes.All.Select(s => s.Name));
    }

    [Fact]
    public async Task Test_Pass_WithoutGrantedScopes_ConfirmsTheScopesTheProbesUsed()
    {
        _api.Map(Root + AttachmentsPath, _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await _tester.TestConnectionAsync(_request);

        ((string[])result.Details!["scopes"]).Should().Equal(AtlassianSiteScopes.ProvenByTest);
        AtlassianSiteScopes.ProvenByTest.Should().BeEquivalentTo(
            AtlassianSiteScopes.All.Where(s => !s.Optional).Select(s => s.Name), "every required scope is probed");
    }

    [Fact]
    public async Task Test_WithoutTheAttachmentScope_StillPasses_AndLeavesItUnconfirmed()
    {
        _api.TokenScope = string.Join(' ', AtlassianSiteScopes.All.Where(s => !s.Optional).Select(s => s.Name));
        _api.Map(Root + AttachmentsPath, _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeTrue();
        ((string[])result.Details!["scopes"]).Should().NotContain(AtlassianSiteScopes.Attachment);
    }

    [Fact]
    public async Task Test_AttachmentProbePasses_ConfirmsTheAttachmentScope()
    {
        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeTrue();
        ((string[])result.Details!["scopes"]).Should().Contain(AtlassianSiteScopes.Attachment);
        _api.Calls.Should().ContainKey(Root + AttachmentsPath);
    }

    [Theory]
    [InlineData(CommentsPath, "read:comment:confluence", HttpStatusCode.Forbidden)]
    [InlineData(CommentsPath, "read:comment:confluence", HttpStatusCode.Unauthorized)]
    [InlineData(FolderPath, "read:folder:confluence", HttpStatusCode.Forbidden)]
    [InlineData("/rest/api/user/bulk", "read:content-details:confluence", HttpStatusCode.Forbidden)]
    [InlineData("/rest/api/search", "read:content-details:confluence", HttpStatusCode.Unauthorized)]
    public async Task Test_CredentialMissingAScopeASyncNeeds_FailsAtTheContentStep_NamingTheScope(
        string path, string scope, HttpStatusCode status)
    {
        _api.Map(Root + path, _ => FakeAtlassianApi.Json(new { code = (int)status, message = "Unauthorized; scope does not match" }, status));

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse("a credential that will fail at sync must not show Working");
        result.Details!["step"].Should().Be(AtlassianConnectionTester.ContentStep);
        result.Details["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.MissingScope);
        result.Message.Should().Contain($"missing the {scope} scope").And.Contain("Create a new OAuth 2.0 credential");
        _api.Calls.Keys.Should().NotContain(p => p.EndsWith("/permission/check"), "the test stops at the first missing scope");
    }

    [Fact]
    public async Task Test_ContentProbeServerError_IsNotReportedAsAMissingScope()
    {
        _api.Map(Root + CommentsPath, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Details!["step"].Should().Be(AtlassianConnectionTester.ContentStep);
        result.Details["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.General);
        result.Message.Should().Contain("Couldn't check the read:comment:confluence scope");
    }

    [Fact]
    public async Task Test_FolderProbe_NotFound_StillProvesTheScope()
    {
        _api.Map(Root + FolderPath, _ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Test_PageInAFolder_ProbesThatFolder()
    {
        _api.MapJson(Root + "/api/v2/pages", new { results = new[] { new { id = "4242", parentId = "777", parentType = "folder" } } });
        _api.MapJson(Root + "/api/v2/folders/777", new { id = "777", title = "Team" });

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeTrue();
        _api.Calls.Should().ContainKey(Root + "/api/v2/folders/777");
        _api.Calls.Should().NotContainKey(Root + FolderPath);
    }

    [Fact]
    public async Task Test_BadSecret_IsMarkedRejected_WithAtlassiansErrorCodeButNotTheSecret()
    {
        _api.FailTokenWith(HttpStatusCode.Unauthorized, """{"error":"access_denied","error_description":"Unauthorized"}""");

        const string secret = "S3cr3t-Value-XYZ";

        var result = await _tester.TestConnectionAsync(_request with { ClientSecret = secret });

        result.Details!["rejected"].Should().Be(true);
        result.Details["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.CredentialsRejected);
        ((string)result.Details["detail"]).Should().Contain("HTTP 401").And.Contain("access_denied");
        result.Details.Values.OfType<string>().Should().NotContain(v => v.Contains(secret));
        result.Message.Should().NotContain(secret);
    }

    public enum TokenAnswer { Refused, Redirect, NoAccessToken }

    // Only a refusal says the client ID or secret is wrong; a redirect from a proxy or a token
    // answer with no token says nothing about them, so it must not ask for new credentials.
    [Theory]
    [InlineData(TokenAnswer.Refused, true)]
    [InlineData(TokenAnswer.Redirect, false)]
    [InlineData(TokenAnswer.NoAccessToken, false)]
    public async Task Test_SignInStep_OnlyARefusalIsRejectedCredentials(TokenAnswer answer, bool rejected)
    {
        switch (answer)
        {
            case TokenAnswer.Refused: _api.FailTokenWith(HttpStatusCode.Unauthorized, """{"error":"access_denied"}"""); break;
            case TokenAnswer.Redirect: _api.FailTokenWith(HttpStatusCode.Redirect); break;
            default: _api.AnswerTokenWith("{}"); break;
        }

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Details!["step"].Should().Be(AtlassianConnectionTester.TokenStep);
        if (rejected)
        {
            result.Details["rejected"].Should().Be(true);
            result.Details["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.CredentialsRejected);
            result.Message.Should().Contain("rejected the client ID or secret");
        }
        else
        {
            result.Details.Should().NotContainKey("rejected");
            result.Details["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.General);
            result.Message.Should().Contain("didn't answer properly").And.NotContain("rejected");
            result.Details.Should().ContainKey("detail");
        }
    }

    [Fact]
    public async Task Test_EveryFailure_CarriesAStepTroubleshootingEntryAndItsDuration()
    {
        _api.Map(Root + "/api/v2/spaces", _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await _tester.TestConnectionAsync(_request);

        result.Details!["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.NoSpaces);
        result.Details["stepMs"].Should().BeOfType<long>();
        result.Details["detail"].Should().Be("HTTP 403 (Forbidden)");
        result.Message.Should().Contain("Viewer role");
    }

    [Theory]
    [InlineData("site", "Finding the site")]
    [InlineData("token", "Signing in as the service account")]
    [InlineData("identity", "Reading Confluence")]
    [InlineData("spaces", "Listing spaces")]
    [InlineData("content", "Reading comments, folders and changes")]
    [InlineData("admin", "Checking access permissions")]
    public void StepLabel_IsPlainWords(string step, string label) =>
        AtlassianConnectionTester.StepLabel(step).Should().Be(label);

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
        result.Details["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.NothingToCheckYet);
        result.Details["cloudId"].Should().Be(CloudId);
        result.Details["siteUrl"].Should().Be("https://acme.atlassian.net");
        result.Message.Should().Contain("save anyway");
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
        result.Details["warning"].Should().Be(true);
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

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "limiting requests")]
    [InlineData(HttpStatusCode.InternalServerError, "didn't answer properly")]
    public async Task Test_SignInStepRateLimitedOrUnanswered_PointsAtTheGeneralSection_NotRejectedCredentials(
        HttpStatusCode status, string says)
    {
        _api.FailTokenWith(status);

        var result = await _tester.TestConnectionAsync(_request);

        result.Details!["step"].Should().Be(AtlassianConnectionTester.TokenStep);
        result.Details["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.General);
        result.Details.Should().NotContainKey("rejected");
        result.Message.Should().Contain(says).And.Contain("test again");
    }

    [Fact]
    public async Task Test_SignInStepTimesOut_FailsWithoutThrowing_AndPointsAtTheGeneralSection()
    {
        var tester = new AtlassianConnectionTester(new TimingOutTokenFactory(_api));

        var result = await tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Details!["step"].Should().Be(AtlassianConnectionTester.TokenStep);
        result.Details["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.General);
        result.Message.Should().Contain("didn't answer in time");
    }

    /// <summary>Atlassian's token endpoint never answers: the HTTP client's own timeout fires.</summary>
    private sealed class TimingOutTokenFactory(FakeAtlassianApi api) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(api), disposeHandler: true);

        private sealed class Handler(FakeAtlassianApi api) : DelegatingHandler(api)
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                request.RequestUri!.AbsolutePath == FakeAtlassianApi.TokenPath
                    ? throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout")
                    : base.SendAsync(request, ct);

            // The fake is shared with the test class, which disposes it.
            protected override void Dispose(bool disposing) { }
        }
    }

    private sealed class FakeFactory(FakeAtlassianApi api) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => api.CreateClient();
    }
}
