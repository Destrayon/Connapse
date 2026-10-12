using System.Text.Json;
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

    private readonly FakeAtlassianApi _api = new();
    private readonly IConnectionStore _store = Substitute.For<IConnectionStore>();
    private readonly List<(string Action, string? Id, string Details)> _events = [];
    private readonly AtlassianSiteService _sites;

    public AtlassianSiteServiceTests()
    {
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => _api.CreateClient());

        var audit = Substitute.For<IAuditLogger>();
        audit.LogAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(c => _events.Add((c.ArgAt<string>(0), c.ArgAt<string?>(2), JsonSerializer.Serialize(c.ArgAt<object?>(3)))));

        _sites = new AtlassianSiteService(http, new AtlassianConnectionTester(http), _store, audit,
            new AtlassianTokenSource(http, TimeProvider.System));

        _api.MapJson(FakeAtlassianApi.TenantInfoPath, new { cloudId = CloudId });
        _api.MapJson(Root + "/rest/api/user/current", new { accountId = "svc" });
        _api.MapJson(Root + "/api/v2/spaces", new { results = new[] { new { id = "1" } } });
        _api.MapJson(Root + "/api/v2/pages", new { results = new[] { new { id = "7" } } });
        _api.MapJson(Root + "/rest/api/search/user", new { results = new[] { new { user = new { accountId = "other" } } } });
        _api.MapJson(Root + "/rest/api/content/7/permission/check", new { hasPermission = true });

        _store.CreateAsync(Arg.Any<CreateConnectionRequest>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(c => Site(c.Arg<CreateConnectionRequest>().Name));
    }

    public void Dispose() => _api.Dispose();

    private static Connection Site(string name = "acme.atlassian.net", string clientId = "client-1") => new(
        Guid.NewGuid(), name, ConnectionProvider.Atlassian,
        $$"""{"siteUrl":"https://{{name}}","cloudId":"{{CloudId}}","clientId":"{{clientId}}"}""",
        null, DateTime.UtcNow, DateTime.UtcNow, HasSecret: true, SourceCount: 3);

    [Fact]
    public async Task Add_WhenAStepFails_AuditsTheStartAndTheFailedStepWithItsDuration_AndNoSecret()
    {
        _api.Map(Root + "/api/v2/spaces", _ => new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));

        var added = await _sites.AddAsync("https://acme.atlassian.net", "client-1", Secret, null);

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
        var added = await _sites.AddAsync("https://acme.atlassian.net", "client-1", Secret, null);

        added.Outcome.Should().Be(AtlassianAddOutcome.Created);
        _events.Select(e => e.Action).Should().Equal("connection.test_started", "connection.test_verified", "connection.created");
        JsonDocument.Parse(_events[1].Details).RootElement.GetProperty("TotalMs").ValueKind.Should().Be(JsonValueKind.Number);
        await _store.Received(1).RecordHealthAsync(added.Connection!.Id,
            Arg.Is<ConnectionHealth>(h => h.Succeeded && h.ConfirmedScopes!.Count > 0), Arg.Any<CancellationToken>());
        _events.Should().OnlyContain(e => !e.Details.Contains(Secret));
    }

    [Fact]
    public async Task Add_SiteWithNoPages_IsNotSavedUnlessAskedTo()
    {
        _api.MapJson(Root + "/api/v2/pages", new { results = Array.Empty<object>() });

        var refused = await _sites.AddAsync("https://acme.atlassian.net", "client-1", Secret, null);
        var saved = await _sites.AddAsync("https://acme.atlassian.net", "client-1", Secret, null, saveAnyway: true);

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

        var added = await _sites.AddAsync("https://acme.atlassian.net", "client-1", Secret, null, saveAnyway: true);

        added.Outcome.Should().Be(AtlassianAddOutcome.TestFailed);
        await _store.DidNotReceiveWithAnyArgs().CreateAsync(default!, default, default);
    }

    [Fact]
    public async Task Replace_WhenTheNewCredentialPasses_UpdatesSecretAndClientIdInPlace()
    {
        Connection site = Site();
        _store.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        _store.UpdateAsync(site.Id, Arg.Any<UpdateConnectionRequest>(), Arg.Any<CancellationToken>()).Returns(site);

        var replaced = await _sites.ReplaceCredentialsAsync(site.Id, " client-2 ", "new-secret", null);

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

        var replaced = await _sites.ReplaceCredentialsAsync(site.Id, "client-2", "wrong", null);

        replaced.Outcome.Should().Be(AtlassianReplaceOutcome.TestFailed);
        replaced.Test!.Details!["step"].Should().Be("token");
        await _store.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
        await _store.DidNotReceiveWithAnyArgs().RecordHealthAsync(default, default!, default);
    }

    [Fact]
    public async Task Replace_WhenTheAddressNowBelongsToAnotherSite_RefusesIt()
    {
        Connection site = Site() with { ConfigJson = """{"siteUrl":"https://acme.atlassian.net","cloudId":"99999999-2222-3333-4444-555555555555","clientId":"client-1"}""" };
        _store.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);

        var replaced = await _sites.ReplaceCredentialsAsync(site.Id, "client-2", "new-secret", null);

        replaced.Outcome.Should().Be(AtlassianReplaceOutcome.SiteChanged);
        await _store.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
    }

    [Fact]
    public async Task Retest_RecordsARejectedCredentialAsNeedingNewCredentials()
    {
        Connection site = Site();
        _store.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        _store.GetSecretAsync(site.Id, Arg.Any<CancellationToken>()).Returns(Secret);
        _api.FailTokenWith(System.Net.HttpStatusCode.Unauthorized);

        var result = await _sites.RetestAsync(site.Id);

        result!.Success.Should().BeFalse();
        await _store.Received(1).RecordHealthAsync(site.Id,
            Arg.Is<ConnectionHealth>(h => h.CredentialRejected && h.Origin == "test" && h.Step == "token"), Arg.Any<CancellationToken>());
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
        _store.GetSecretAsync(site.Id, Arg.Any<CancellationToken>()).Returns(Secret);
        if (redirect)
            _api.FailTokenWith(System.Net.HttpStatusCode.Redirect);
        else
            _api.AnswerTokenWith("{}");

        var result = await _sites.RetestAsync(site.Id);

        result!.Success.Should().BeFalse();
        await _store.Received(1).RecordHealthAsync(site.Id,
            Arg.Is<ConnectionHealth>(h => !h.CredentialRejected && !h.Succeeded && h.Step == "token"), Arg.Any<CancellationToken>());
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
