using System.Security.Claims;
using System.Text.Json;
using Connapse.Application;
using Connapse.Application.Connections;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Connectors;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Connectors.Atlassian;
using Connapse.Web.Services;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Connapse.Core.Tests.Web;

/// <summary>
/// The site service records what each test found (as the site's health and as audit events that
/// make setup measurable), saves a warning only when asked to, and replaces a credential in place.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AtlassianSiteServiceTests : IDisposable
{
    private const string CloudId = "11111111-2222-3333-4444-555555555555";
    private const string Root = "/ex/confluence/" + CloudId + "/wiki";
    private const string Secret = "atlassian-secret-value-123";

    private static readonly OperationContext Caller = new(new ClaimsPrincipal(new ClaimsIdentity("test")), OperationSurface.Ui);

    private readonly FakeAtlassianApi _api = new();
    private readonly IConnectionStore _store = Substitute.For<IConnectionStore>();
    private readonly IOperationAuthorizer _authorizer = Substitute.For<IOperationAuthorizer>();
    private readonly List<(string Action, string? Id, string Details)> _events = [];
    private readonly List<Connection> _saved = [];
    private readonly AtlassianSiteService _sites;

    public AtlassianSiteServiceTests()
    {
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => _api.CreateClient());

        var audit = Substitute.For<IAuditLogger>();
        audit.LogAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(c => _events.Add((c.ArgAt<string>(0), c.ArgAt<string?>(2), JsonSerializer.Serialize(c.ArgAt<object?>(3)))));

        var authorizer = _authorizer;
        authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(true);
        _sites = new AtlassianSiteService(http, new AtlassianConnectionTester(http), _store, audit,
            new AtlassianTokenSource(http, TimeProvider.System),
            new CreateConnection(_store, Substitute.For<IConnectionSettingsRules>(), audit, authorizer),
            new ReplaceConnectionSecret(_store, Substitute.For<ISourceStore>(), Substitute.For<IConnectionSettingsRules>(), audit, authorizer));

        _api.MapJson(FakeAtlassianApi.TenantInfoPath, new { cloudId = CloudId });
        _api.MapJson(Root + "/rest/api/user/current", new { accountId = "svc" });
        _api.MapJson(Root + "/api/v2/spaces", new { results = new[] { new { id = "1" } } });
        _api.MapJson(Root + "/api/v2/pages", new { results = new[] { new { id = "7" } } });
        _api.MapJson(Root + "/rest/api/search/user", new { results = new[] { new { user = new { accountId = "other" } } } });
        _api.MapJson(Root + "/rest/api/content/7/permission/check", new { hasPermission = true });

        _store.CreateAsync(Arg.Any<CreateConnectionRequest>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                var request = c.Arg<CreateConnectionRequest>();
                var saved = Site(request.Name) with { ConfigJson = request.ConfigJson };
                _saved.Add(saved);
                return saved;
            });
        _store.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyList<Connection>)[.. _saved]);
    }

    public void Dispose() => _api.Dispose();

    private static Connection Site(string name = "acme.atlassian.net", string clientId = "client-1") => new(
        Guid.NewGuid(), name, ConnectionProvider.Atlassian,
        $$"""{"siteUrl":"https://{{name}}","cloudId":"{{CloudId}}","clientId":"{{clientId}}"}""",
        null, DateTime.UtcNow, DateTime.UtcNow, HasSecret: true, SourceCount: 3);

    private static Connection JiraSite() => new(
        Guid.NewGuid(), "acme.atlassian.net (Jira)", ConnectionProvider.Atlassian,
        $$"""{"siteUrl":"https://acme.atlassian.net","cloudId":"{{CloudId}}","clientId":"client-1","product":"jira"}""",
        null, DateTime.UtcNow, DateTime.UtcNow, HasSecret: true, SourceCount: 1);

    [Fact]
    public async Task Add_WhenAStepFails_AuditsTheStartAndTheFailedStepWithItsDuration_AndNoSecret()
    {
        _api.Map(Root + "/api/v2/spaces", _ => new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));

        var added = await _sites.AddAsync(Caller, "https://acme.atlassian.net", "client-1", Secret);

        added.Outcome.Should().Be(AtlassianAddOutcome.TestFailed);
        _events.Select(e => e.Action).Should().Equal("connection.test_started", "connection.test_step_failed");
        var failed = JsonDocument.Parse(_events[1].Details).RootElement;
        failed.GetProperty("Step").GetString().Should().Be("spaces");
        failed.GetProperty("StepMs").ValueKind.Should().Be(JsonValueKind.Number);
        failed.GetProperty("TotalMs").ValueKind.Should().Be(JsonValueKind.Number);
        failed.GetProperty("Attempt").GetGuid()
            .Should().Be(JsonDocument.Parse(_events[0].Details).RootElement.GetProperty("Attempt").GetGuid());
        _events.Should().OnlyContain(e => !e.Details.Contains(Secret));
    }

    [Fact]
    public async Task Add_WhenEveryStepPasses_AuditsVerifiedWithTheTotalDuration_AndRecordsHealth()
    {
        var added = await _sites.AddAsync(Caller, "https://acme.atlassian.net", "client-1", Secret);

        added.Outcome.Should().Be(AtlassianAddOutcome.Created);
        _events.Select(e => e.Action).Should().Equal("connection.test_started", "connection.test_verified", "connection.created");
        JsonDocument.Parse(_events[1].Details).RootElement.GetProperty("TotalMs").ValueKind.Should().Be(JsonValueKind.Number);
        await _store.Received(1).RecordHealthAsync(added.Connection!.Id,
            Arg.Is<ConnectionHealth>(h => h.Succeeded && h.ConfirmedScopes!.Count > 0), Arg.Any<CancellationToken>());
        _events.Should().OnlyContain(e => !e.Details.Contains(Secret));
    }

    [Fact]
    public async Task Add_Jira_TestsJiraOnly_AndConfirmsJirasScopes()
    {
        new FakeJira(_api, CloudId).MapSiteTest();
        // The site's Confluence refuses this credential: a Jira connection never asks it.
        _api.Map(Root + "/rest/api/user/current", _ => new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));

        var added = await _sites.AddAsync(Caller, "https://acme.atlassian.net", "client-1", Secret, product: AtlassianProduct.Jira);

        added.Outcome.Should().Be(AtlassianAddOutcome.Created);
        _api.Calls.Keys.Should().NotContain(k => k.StartsWith("/ex/confluence/"));
        await _store.Received(1).RecordHealthAsync(added.Connection!.Id,
            Arg.Is<ConnectionHealth>(h => h.Succeeded && h.ConfirmedScopes!.Count > 0 && h.ConfirmedScopes.All(s => s.EndsWith(":jira"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Add_Confluence_WhenOnlyJiraWorks_IsNotSaved()
    {
        new FakeJira(_api, CloudId).MapSiteTest();
        _api.Map(Root + "/rest/api/user/current", _ => new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));

        var added = await _sites.AddAsync(Caller, "https://acme.atlassian.net", "client-1", Secret);

        added.Outcome.Should().Be(AtlassianAddOutcome.TestFailed);
        added.Test!.Details!["step"].Should().Be(AtlassianConnectionTester.IdentityStep);
        _api.Calls.Keys.Should().NotContain(k => k.StartsWith("/ex/jira/"));
        await _store.DidNotReceiveWithAnyArgs().CreateAsync(default!, default, default);
    }

    [Fact]
    public async Task Retest_JiraConnection_TestsJira()
    {
        new FakeJira(_api, CloudId).MapSiteTest();
        Connection site = JiraSite();
        _store.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        _store.GetSecretAtAsync(site.Id, site.UpdatedAt, Arg.Any<CancellationToken>()).Returns(Secret);

        var result = (await _sites.TestStoredAsync(site.Id)).Result;

        result!.Success.Should().BeTrue();
        result.Message.Should().Contain("read Jira");
        _api.Calls.Keys.Should().NotContain(k => k.StartsWith("/ex/confluence/"));
    }

    [Fact]
    public async Task Replace_JiraConnection_TestsTheNewCredentialAgainstJira()
    {
        var jira = new FakeJira(_api, CloudId).MapSiteTest();
        jira.Answer("/myself", System.Net.HttpStatusCode.Forbidden);
        Connection site = JiraSite();
        _store.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);

        var replaced = await _sites.ReplaceCredentialsAsync(Caller, site.Id, "client-2", "new-secret");

        replaced.Outcome.Should().Be(AtlassianReplaceOutcome.TestFailed);
        replaced.Test!.Details!["step"].Should().Be(AtlassianConnectionTester.JiraIdentityStep);
        await _store.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
    }

    [Fact]
    public async Task Add_SiteWithNoPages_IsNotSavedUnlessAskedTo()
    {
        _api.MapJson(Root + "/api/v2/pages", new { results = Array.Empty<object>() });

        var refused = await _sites.AddAsync(Caller, "https://acme.atlassian.net", "client-1", Secret);
        var saved = await _sites.AddAsync(Caller, "https://acme.atlassian.net", "client-1", Secret, saveAnyway: true);

        refused.Outcome.Should().Be(AtlassianAddOutcome.TestFailed);
        saved.Outcome.Should().Be(AtlassianAddOutcome.Created);
        await _store.Received(1).CreateAsync(Arg.Any<CreateConnectionRequest>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await _store.Received(1).RecordHealthAsync(Arg.Any<Guid>(), Arg.Is<ConnectionHealth>(h => h.Warning && !h.Succeeded),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Add_SaveAnyway_NeverSavesARealFailure()
    {
        _api.FailTokenWith(System.Net.HttpStatusCode.Unauthorized);

        var added = await _sites.AddAsync(Caller, "https://acme.atlassian.net", "client-1", Secret, saveAnyway: true);

        added.Outcome.Should().Be(AtlassianAddOutcome.TestFailed);
        await _store.DidNotReceiveWithAnyArgs().CreateAsync(default!, default, default);
    }

    [Fact]
    public async Task Add_SameSiteBothProducts_Allowed()
    {
        new FakeJira(_api, CloudId).MapSiteTest();
        var confluence = await _sites.AddAsync(Caller, "https://acme.atlassian.net", "client-1", Secret);
        var jira = await _sites.AddAsync(Caller, "https://acme.atlassian.net", "client-1", Secret, product: AtlassianProduct.Jira);

        confluence.Outcome.Should().Be(AtlassianAddOutcome.Created);
        jira.Outcome.Should().Be(AtlassianAddOutcome.Created);
        AtlassianSite.FromConfigJson(confluence.Connection!.ConfigJson)
            .Should().Be(new AtlassianSite("https://acme.atlassian.net", CloudId, "client-1"));
        AtlassianSite.FromConfigJson(jira.Connection!.ConfigJson)
            .Should().Be(new AtlassianSite("https://acme.atlassian.net", CloudId, "client-1", AtlassianProduct.Jira));
        confluence.Connection.Name.Should().Be("acme.atlassian.net");
        jira.Connection.Name.Should().NotBe(confluence.Connection.Name);
    }

    [Fact]
    public async Task Add_SameSiteSameProduct_Duplicate()
    {
        new FakeJira(_api, CloudId).MapSiteTest();
        var first = await _sites.AddAsync(Caller, "https://acme.atlassian.net", "client-1", Secret, product: AtlassianProduct.Jira);
        // Renamed, so only the site and product can tell it is the same connection.
        _saved[0] = _saved[0] with { Name = "Our Jira" };

        var second = await _sites.AddAsync(Caller, "https://acme.atlassian.net", "client-2", Secret, product: AtlassianProduct.Jira);

        first.Outcome.Should().Be(AtlassianAddOutcome.Created);
        second.Outcome.Should().Be(AtlassianAddOutcome.Conflict);
        second.Name.Should().Be("Our Jira");
        await _store.Received(1).CreateAsync(Arg.Any<CreateConnectionRequest>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListSpaces_JiraConnection_RefusedWithoutARequest()
    {
        Connection jira = Site() with
        {
            ConfigJson = $$"""{"siteUrl":"https://acme.atlassian.net","cloudId":"{{CloudId}}","clientId":"client-1","product":"jira"}""",
        };
        _store.GetAsync(jira.Id, Arg.Any<CancellationToken>()).Returns(jira);

        var listed = await _sites.ListSpacesAsync(jira.Id, includePersonal: false);

        listed.Outcome.Should().Be(AtlassianSpacesOutcome.WrongProduct);
        listed.Error.Should().Be("This is a Jira connection; list its projects instead.");
        _api.Requests.Should().BeEmpty();
        await _store.DidNotReceiveWithAnyArgs().GetSecretAsync(default, default);
    }

    [Fact]
    public async Task ListProjects_ConfluenceConnection_RefusedWithoutARequest()
    {
        Connection confluence = Site();
        _store.GetAsync(confluence.Id, Arg.Any<CancellationToken>()).Returns(confluence);

        var listed = await _sites.ListProjectsAsync(confluence.Id);

        listed.Outcome.Should().Be(AtlassianSpacesOutcome.WrongProduct);
        listed.Error.Should().Be("This is a Confluence connection; list its spaces instead.");
        _api.Requests.Should().BeEmpty();
        await _store.DidNotReceiveWithAnyArgs().GetSecretAsync(default, default);
    }

    [Fact]
    public async Task Replace_WhenTheNewCredentialPasses_UpdatesSecretAndClientIdInPlace()
    {
        Connection site = Site();
        _store.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        _store.UpdateAsync(site.Id, Arg.Any<UpdateConnectionRequest>(), Arg.Any<CancellationToken>()).Returns(site);

        var replaced = await _sites.ReplaceCredentialsAsync(Caller, site.Id, " client-2 ", "new-secret");

        replaced.Outcome.Should().Be(AtlassianReplaceOutcome.Replaced);
        await _store.Received(1).UpdateAsync(site.Id, Arg.Is<UpdateConnectionRequest>(r =>
            r.Secret == "new-secret" && r.Name == null
            && AtlassianSite.FromConfigJson(r.ConfigJson) == new AtlassianSite("https://acme.atlassian.net", CloudId, "client-2")),
            Arg.Any<CancellationToken>());
        _events.Select(e => e.Action).Should().Contain("connection.credentials_replaced");
        _events.Should().OnlyContain(e => !e.Details.Contains("new-secret"));
    }

    [Fact]
    public async Task Replace_WhenTheNewCredentialFails_ChangesNothing()
    {
        Connection site = Site();
        _store.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        _api.FailTokenWith(System.Net.HttpStatusCode.Unauthorized);

        var replaced = await _sites.ReplaceCredentialsAsync(Caller, site.Id, "client-2", "wrong");

        replaced.Outcome.Should().Be(AtlassianReplaceOutcome.TestFailed);
        replaced.Test!.Details!["step"].Should().Be("token");
        await _store.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
        await _store.DidNotReceiveWithAnyArgs().RecordHealthAsync(default, default!, default);
    }

    [Fact]
    public async Task Replace_NotAnAdministrator_IsForbiddenBeforeAtlassianIsAsked()
    {
        Connection site = Site();
        _store.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.SourcesAdmin).Returns(false);

        var replaced = await _sites.ReplaceCredentialsAsync(Caller, site.Id, "client-2", "new-secret");

        replaced.Outcome.Should().Be(AtlassianReplaceOutcome.Forbidden);
        _api.Requests.Should().BeEmpty();
        await _store.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
    }

    [Fact]
    public async Task Replace_SiteEditedWhileTheCredentialWasChecked_StoresNothing()
    {
        Connection site = Site();
        _store.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        _store.UpdateAsync(site.Id, Arg.Any<UpdateConnectionRequest>(), Arg.Any<CancellationToken>())
            .Returns<Connection?>(_ => throw new ConnectionChangedException(site.Id));

        var replaced = await _sites.ReplaceCredentialsAsync(Caller, site.Id, "client-2", "new-secret");

        replaced.Outcome.Should().Be(AtlassianReplaceOutcome.Changed);
        await _store.Received(1).UpdateAsync(site.Id, Arg.Is<UpdateConnectionRequest>(r => r.ExpectedUpdatedAt == site.UpdatedAt),
            Arg.Any<CancellationToken>());
        _events.Select(e => e.Action).Should().NotContain("connection.credentials_replaced");
    }

    [Fact]
    public async Task Replace_WhenTheAddressNowBelongsToAnotherSite_RefusesIt()
    {
        Connection site = Site() with { ConfigJson = """{"siteUrl":"https://acme.atlassian.net","cloudId":"99999999-2222-3333-4444-555555555555","clientId":"client-1"}""" };
        _store.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);

        var replaced = await _sites.ReplaceCredentialsAsync(Caller, site.Id, "client-2", "new-secret");

        replaced.Outcome.Should().Be(AtlassianReplaceOutcome.SiteChanged);
        await _store.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
    }

    [Fact]
    public async Task Retest_RecordsARejectedCredentialAsNeedingNewCredentials()
    {
        Connection site = Site();
        _store.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        _store.GetSecretAtAsync(site.Id, site.UpdatedAt, Arg.Any<CancellationToken>()).Returns(Secret);
        _api.FailTokenWith(System.Net.HttpStatusCode.Unauthorized);

        var run = await _sites.TestStoredAsync(site.Id);

        run.Result!.Success.Should().BeFalse();
        run.Health.Should().Match<ConnectionHealth>(h => h.CredentialRejected && h.Origin == "test" && h.Step == "token");
        _events.Select(e => e.Action).Should().Equal("connection.test_started", "connection.test_step_failed");
        _events.Should().OnlyContain(e => e.Id == site.Id.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retest_RedirectOrNoToken_RecordsAFailedCheck_NotRejectedCredentials(bool redirect)
    {
        Connection site = Site();
        _store.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        _store.GetSecretAtAsync(site.Id, site.UpdatedAt, Arg.Any<CancellationToken>()).Returns(Secret);
        if (redirect)
            _api.FailTokenWith(System.Net.HttpStatusCode.Redirect);
        else
            _api.AnswerTokenWith("{}");

        var run = await _sites.TestStoredAsync(site.Id);

        run.Result!.Success.Should().BeFalse();
        run.Health.Should().Match<ConnectionHealth>(h => !h.CredentialRejected && !h.Succeeded && h.Step == "token");
    }

    [Fact]
    public async Task Retest_RateLimited_RecordsNoHealth()
    {
        Connection site = Site();
        _store.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        _store.GetSecretAtAsync(site.Id, site.UpdatedAt, Arg.Any<CancellationToken>()).Returns(Secret);
        _api.FailTokenWith(System.Net.HttpStatusCode.TooManyRequests);

        var run = await _sites.TestStoredAsync(site.Id);

        run.Result!.Success.Should().BeFalse();
        run.Health.Should().BeNull();
    }

    [Fact]
    public async Task Retest_SavedAfterTheSiteWasRead_IsAConflictAndSendsNothing()
    {
        Connection site = Site();
        _store.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        _store.GetSecretAtAsync(site.Id, site.UpdatedAt, Arg.Any<CancellationToken>())
            .Returns<string?>(_ => throw new ConnectionChangedException(site.Id));

        var run = await _sites.TestStoredAsync(site.Id);

        run.Result.Should().BeNull();
        run.Refusal.Should().Be(ConnectionStatus.Conflict);
        _api.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Report_UsesPlainLabelsAndTheGuidesAnchors()
    {
        var report = AtlassianTestReport.From(ConnectionTestResult.CreateFailure("m",
            new Dictionary<string, object> { ["step"] = "admin", ["troubleshoot"] = "missing-scope", ["detail"] = "HTTP 403" }));

        report.StepLabel.Should().Be("Checking access permissions");
        report.TroubleshootingUrl.Should().Be("https://github.com/Destrayon/Connapse/blob/main/docs/atlassian-setup.md#missing-scope");
        report.Detail.Should().Be("HTTP 403");
    }
}
