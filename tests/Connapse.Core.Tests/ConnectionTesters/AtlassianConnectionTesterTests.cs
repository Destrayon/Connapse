using System.Net;
using Connapse.Core.Tests.Connectors;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Connectors.Atlassian;
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
    private readonly FakeJira _jira;
    private readonly AtlassianConnectionTester _tester;
    private readonly AtlassianSiteTestRequest _request = new("https://acme.atlassian.net", "client-1", "secret");
    private readonly AtlassianSiteTestRequest _jiraRequest =
        new("https://acme.atlassian.net", "client-1", "secret", AtlassianProduct.Jira);

    public AtlassianConnectionTesterTests()
    {
        _tester = new AtlassianConnectionTester(new FakeFactory(_api));
        // Jira answers 404 everywhere until a test maps it.
        _jira = new FakeJira(_api, CloudId);
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
    public async Task Test_ConfluencePasses_DetailsAreTheSiteAndItsScopesOnly()
    {
        _jira.MapSiteTest();

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeTrue();
        result.Message.Should().Be("Connected to https://acme.atlassian.net: found the site, signed in as the service account, "
            + "read Confluence, listed spaces, read comments, folders and changes, and confirmed it can check what other people may read.");
        result.Details!.Keys.Should().BeEquivalentTo("cloudId", "siteUrl", "scopes");
        ((string[])result.Details["scopes"]).Should().OnlyContain(s => s.EndsWith(":confluence"));
        _api.Calls.Keys.Should().NotContain(k => k.StartsWith("/ex/jira/"), "a Confluence connection never asks Jira");
    }

    [Fact]
    public async Task Test_ConfluenceWarns_DetailsAreAsBefore()
    {
        _api.MapJson(Root + "/api/v2/pages", new { results = Array.Empty<object>() });

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Details!.Keys.Should().BeEquivalentTo("step", "troubleshoot", "stepMs", "warning", "siteUrl", "cloudId");
        result.Details["step"].Should().Be(AtlassianConnectionTester.AdminStep);
    }

    [Fact]
    public async Task Test_ConfluenceFails_DetailsAreAsBefore()
    {
        _api.Map(Root + "/api/v2/spaces", _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await _tester.TestConnectionAsync(_request);

        result.Success.Should().BeFalse();
        result.Message.Should().Be(ConfluenceSiteProbe.NoSpacesMessage);
        result.Details!.Keys.Should().BeEquivalentTo("step", "troubleshoot", "stepMs", "detail");
        result.Details["step"].Should().Be(AtlassianConnectionTester.SpacesStep);
        result.Details["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.NoSpaces);
        result.Details["detail"].Should().Be("HTTP 403 (Forbidden)");
    }

    [Fact]
    public async Task Test_JiraAllProbesPass_Succeeds()
    {
        _jira.MapSiteTest();

        var result = await _tester.TestConnectionAsync(_jiraRequest);

        result.Success.Should().BeTrue();
        result.Message.Should().Be("Connected to https://acme.atlassian.net: found the site, signed in as the service account, "
            + "read Jira, listed projects, read issues and comments, and confirmed it can check who may read each issue and comment.");
        result.Details!.Keys.Should().BeEquivalentTo("cloudId", "siteUrl", "scopes");
        result.Details["cloudId"].Should().Be(CloudId);
        string[] scopes = (string[])result.Details["scopes"];
        scopes.Should().Contain(AtlassianSiteScopes.JiraProvenByTest)
            .And.Contain(AtlassianSiteScopes.JiraAttachment)
            .And.OnlyContain(s => s.EndsWith(":jira"))
            .And.OnlyHaveUniqueItems();
        _api.Calls.Keys.Should().NotContain(k => k.StartsWith("/ex/confluence/"), "a Jira connection never asks Confluence");
        _jira.Searches.Should().ContainSingle().Which.Should().Contain($"project = {FakeJira.ProjectId} ORDER BY updated DESC");
        _jira.PermissionChecks.Should().ContainSingle().Which.Should().Contain(FakeJira.OtherAccount)
            .And.Contain("BROWSE_PROJECTS").And.NotContain(FakeJira.OwnAccount).And.NotContain("customer-1");
    }

    [Fact]
    public async Task Test_JiraSiteNotFound_NamesJira()
    {
        _api.MapJson(FakeAtlassianApi.TenantInfoPath, new { cloudId = "not-a-guid" });

        var result = await _tester.TestConnectionAsync(_jiraRequest);

        result.Details!["step"].Should().Be(AtlassianConnectionTester.SiteStep);
        result.Message.Should().Contain("when you open Jira").And.NotContain("Confluence");
    }

    [Fact]
    public async Task Test_JiraMyselfRefused_FailsJiraIdentity()
    {
        _jira.MapSiteTest();
        _jira.Answer("/myself", HttpStatusCode.Forbidden);

        var result = await _tester.TestConnectionAsync(_jiraRequest);

        result.Success.Should().BeFalse();
        result.Message.Should().Be(JiraSiteProbe.CantReadJiraMessage);
        result.Details!["step"].Should().Be(AtlassianConnectionTester.JiraIdentityStep);
        result.Details["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.CantReadJira);
        _api.Calls.Should().NotContainKey(_jira.Root + "/project/search");
    }

    [Fact]
    public async Task Test_JiraCheckForbidden_NotJiraAdministrator()
    {
        _jira.MapSiteTest();
        _jira.Answer("/permissions/check", HttpStatusCode.Forbidden);

        var result = await _tester.TestConnectionAsync(_jiraRequest);

        result.Success.Should().BeFalse();
        result.Message.Should().Be(JiraSiteProbe.NotJiraAdministratorMessage);
        result.Message.Should().Contain("set Jira to User and Jira Administration to App admin")
            .And.Contain("jira-admins-<site> group").And.Contain("Directory > Groups")
            .And.NotContain("admin role");
        result.Details!["step"].Should().Be(AtlassianConnectionTester.JiraAdminStep);
        result.Details["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.NotJiraAdministrator);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "{}")]
    [InlineData(HttpStatusCode.OK, "{\"projectPermissions\":{}}")]
    [InlineData(HttpStatusCode.OK, "<html>Log in</html>")]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    public async Task Test_JiraCheckWithoutAPermissionAnswer_FailsJiraAdminUnverified(HttpStatusCode status, string body)
    {
        _jira.MapSiteTest();
        _api.Map(_jira.Root + "/permissions/check", _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });

        var result = await _tester.TestConnectionAsync(_jiraRequest);

        result.Success.Should().BeFalse();
        result.Details!["step"].Should().Be(AtlassianConnectionTester.JiraAdminStep);
        result.Message.Should().StartWith(JiraSiteProbe.UnverifiedMessage);
    }

    [Fact]
    public async Task Test_JiraGroupsForbidden_CantReadJiraMembership()
    {
        _jira.MapSiteTest();
        _jira.Answer("/user/groups", HttpStatusCode.Forbidden);

        var result = await _tester.TestConnectionAsync(_jiraRequest);

        result.Success.Should().BeFalse();
        result.Message.Should().Be(JiraSiteProbe.CantReadMembershipMessage);
        result.Details!["step"].Should().Be(AtlassianConnectionTester.JiraMembershipStep);
        result.Details["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.CantReadJiraMembership);
    }

    [Theory]
    [InlineData("/project/10000/role")]
    [InlineData("/project/10000/role/10002")]
    public async Task Test_JiraRolesUnreadable_CantReadJiraMembership(string path)
    {
        _jira.MapSiteTest();
        _jira.Answer(path, HttpStatusCode.Forbidden);

        var result = await _tester.TestConnectionAsync(_jiraRequest);

        result.Details!["step"].Should().Be(AtlassianConnectionTester.JiraMembershipStep);
        result.Details["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.CantReadJiraMembership);
    }

    [Fact]
    public async Task Test_JiraRoleUrlWithTrailingSlash_ReadsTheRole()
    {
        _jira.MapSiteTest();
        _api.MapJson(_jira.Root + "/project/10000/role", new Dictionary<string, string>
        {
            ["Developers"] = $"https://api.atlassian.com/ex/jira/{CloudId}/rest/api/3/project/10000/role/10002/",
        });

        var result = await _tester.TestConnectionAsync(_jiraRequest);

        result.Success.Should().BeTrue();
        _api.Calls.GetValueOrDefault(_jira.Root + "/project/10000/role/10002").Should().Be(1);
    }

    [Fact]
    public async Task Test_JiraRoleWithoutActors_CantReadJiraMembership()
    {
        _jira.MapSiteTest();
        _api.MapJson(_jira.Root + "/project/10000/role/10002", new { id = 10002, name = "Developers" });

        var result = await _tester.TestConnectionAsync(_jiraRequest);

        result.Details!["step"].Should().Be(AtlassianConnectionTester.JiraMembershipStep);
    }

    [Fact]
    public async Task Test_JiraRoleListWithoutAReadableRole_CantReadJiraMembership()
    {
        _jira.MapSiteTest();
        _api.MapJson(_jira.Root + "/project/10000/role", new Dictionary<string, string> { ["Developers"] = "not-a-role-address" });

        var result = await _tester.TestConnectionAsync(_jiraRequest);

        result.Success.Should().BeFalse();
        result.Details!["step"].Should().Be(AtlassianConnectionTester.JiraMembershipStep);
        result.Details["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.CantReadJiraMembership);
        result.Details.Should().NotContainKey("scopes");
    }

    [Fact]
    public async Task Test_JiraUserSearchRefused_SaysItCantListUsers()
    {
        _jira.MapSiteTest();
        _jira.Answer("/users/search", HttpStatusCode.Forbidden);

        var result = await _tester.TestConnectionAsync(_jiraRequest);

        result.Message.Should().Be(JiraSiteProbe.CantListUsersMessage);
        result.Message.Should().Contain("read:user:jira").And.Contain("Browse users and groups").And.NotContain("Administer Jira");
        result.Details!["step"].Should().Be(AtlassianConnectionTester.JiraAdminStep);
        result.Details["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.MissingScope);
        _jira.PermissionChecks.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_JiraMyselfWithoutAccountId_FailsJiraIdentityWithoutACheck()
    {
        _jira.MapSiteTest();
        _api.MapJson(_jira.Root + "/myself", new { accountId = "", accountType = "app" });

        var result = await _tester.TestConnectionAsync(_jiraRequest);

        result.Success.Should().BeFalse();
        result.Message.Should().Be(JiraSiteProbe.CantReadJiraMessage);
        result.Details!["step"].Should().Be(AtlassianConnectionTester.JiraIdentityStep);
        _api.Calls.Should().NotContainKey(_jira.Root + "/users/search");
        _jira.PermissionChecks.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_JiraRateLimited_FailsAtTheStepItWasOn()
    {
        _jira.MapSiteTest();
        _api.Intercept = uri => uri.AbsolutePath == _jira.Root + "/issue/bulkfetch"
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : null;

        var result = await _tester.TestConnectionAsync(_jiraRequest);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("rate limiting");
        result.Details!["step"].Should().Be(AtlassianConnectionTester.JiraContentStep);
        result.Details["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.General);
    }

    [Fact]
    public async Task Test_JiraNoOtherUser_Warns()
    {
        _jira.MapSiteTest();
        _jira.Users(
            new { accountId = FakeJira.OwnAccount, accountType = "atlassian" },
            new { accountId = "anonymous", accountType = "atlassian" },
            new { accountId = "addon-1", accountType = "app" });

        var result = await _tester.TestConnectionAsync(_jiraRequest);

        result.Success.Should().BeFalse();
        result.Details!["warning"].Should().Be(true);
        result.Message.Should().Be(JiraSiteProbe.NoOtherUserMessage);
        result.Details["step"].Should().Be(AtlassianConnectionTester.JiraAdminStep);
        result.Details["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.NothingToCheckYet);
        result.Details["cloudId"].Should().Be(CloudId);
        _jira.PermissionChecks.Should().BeEmpty("a check without another account answers for the service account");
    }

    [Fact]
    public async Task Test_JiraNoProjects_Warns()
    {
        _jira.MapSiteTest();
        _api.MapJson(_jira.Root + "/project/search", new { values = Array.Empty<object>(), total = 0, isLast = true });

        var result = await _tester.TestConnectionAsync(_jiraRequest);

        result.Details!["warning"].Should().Be(true);
        result.Message.Should().Be(JiraSiteProbe.NoProjectsMessage);
        result.Details["step"].Should().Be(AtlassianConnectionTester.JiraProjectsStep);
        _jira.Searches.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/search/jql", "read:issue-details:jira")]
    [InlineData("/issue/bulkfetch", "read:issue:jira")]
    [InlineData("/issue/20000/comment", "read:comment:jira")]
    public async Task Test_JiraContentForbidden_FailsJiraContentNamingTheScope(string path, string scope)
    {
        _jira.MapSiteTest();
        _jira.Answer(path, HttpStatusCode.Forbidden);

        var result = await _tester.TestConnectionAsync(_jiraRequest);

        result.Details!["step"].Should().Be(AtlassianConnectionTester.JiraContentStep);
        result.Details["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.MissingScope);
        result.Message.Should().Contain($"missing the {scope} scope");
        _jira.PermissionChecks.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_JiraWithoutTheAttachmentScope_StillPasses()
    {
        _jira.MapSiteTest();
        _jira.Answer("/attachment/meta", HttpStatusCode.Forbidden);

        var result = await _tester.TestConnectionAsync(_jiraRequest);

        result.Success.Should().BeTrue();
        ((string[])result.Details!["scopes"]).Should().NotContain(AtlassianSiteScopes.JiraAttachment);
    }

    [Fact]
    public void Scopes_EveryJiraScopeTaggedJira()
    {
        AtlassianSiteScopes.Jira.Select(s => s.Name).Should().BeEquivalentTo(
            "read:application-role:jira", "read:audit-log:jira", "read:avatar:jira",
            "read:comment:jira", "read:comment.property:jira",
            "read:field:jira", "read:field-configuration:jira", "read:group:jira",
            "read:issue:jira", "read:issue-details:jira", "read:issue-meta:jira", "read:issue-security-level:jira",
            "read:issue-type:jira", "read:issue-type-hierarchy:jira", "read:issue.changelog:jira", "read:issue.vote:jira",
            "read:permission:jira",
            "read:project:jira", "read:project-category:jira", "read:project-role:jira", "read:project-version:jira",
            "read:project.component:jira", "read:project.property:jira",
            "read:status:jira", "read:user:jira",
            "read:attachment:jira");
        AtlassianSiteScopes.Jira.Should().OnlyContain(s => s.Product == "jira" && s.Reason.Length > 0);
        AtlassianSiteScopes.Jira.Where(s => s.Optional).Select(s => s.Name).Should().Equal("read:attachment:jira");
        AtlassianSiteScopes.Confluence.Should().OnlyContain(s => s.Product == "confluence");
        AtlassianSiteScopes.All.Should().Equal(AtlassianSiteScopes.Confluence.Concat(AtlassianSiteScopes.Jira));
        AtlassianSiteScopes.JiraProvenByTest.Should().BeSubsetOf(AtlassianSiteScopes.Jira.Where(s => !s.Optional).Select(s => s.Name));
    }

    [Fact]
    public void Troubleshooting_ListsTheJiraEntries() =>
        AtlassianSiteTroubleshooting.All.Should().Contain(["cant-read-jira", "not-jira-administrator", "cant-read-jira-membership"]);

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
        _api.TokenScope = string.Join(' ', AtlassianSiteScopes.Confluence.Select(s => s.Name));
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
        ((string[])result.Details!["scopes"]).Should().Equal(AtlassianSiteScopes.Confluence.Select(s => s.Name));
    }

    [Fact]
    public async Task Test_Pass_WithoutGrantedScopes_ConfirmsTheScopesTheProbesUsed()
    {
        _api.Map(Root + AttachmentsPath, _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await _tester.TestConnectionAsync(_request);

        ((string[])result.Details!["scopes"]).Should().Equal(AtlassianSiteScopes.ProvenByTest);
        AtlassianSiteScopes.ProvenByTest.Should().BeEquivalentTo(
            AtlassianSiteScopes.Confluence.Where(s => !s.Optional).Select(s => s.Name), "every required scope is probed");
    }

    [Fact]
    public async Task Test_WithoutTheAttachmentScope_StillPasses_AndLeavesItUnconfirmed()
    {
        _api.TokenScope = string.Join(' ', AtlassianSiteScopes.Confluence.Where(s => !s.Optional).Select(s => s.Name));
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
    [InlineData("jira-identity", "Reading Jira")]
    [InlineData("jira-projects", "Listing Jira projects")]
    [InlineData("jira-content", "Reading issues and comments")]
    [InlineData("jira-admin", "Checking Jira access permissions")]
    [InlineData("jira-membership", "Reading Jira groups and project roles")]
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
        result.Details!["step"].Should().Be(AtlassianConnectionTester.IdentityStep);
        result.Details["troubleshoot"].Should().Be(AtlassianSiteTroubleshooting.General);
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
