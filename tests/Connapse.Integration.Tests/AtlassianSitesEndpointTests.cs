using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Connectors;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Connapse.Storage.Connectors.Atlassian;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Adding an Atlassian site through the real host: administrators only, a credential that fails
/// any probe creates nothing, and one that passes is saved as a connection whose secret is
/// encrypted and readable only through the store.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class AtlassianSitesEndpointTests : IAsyncLifetime
{
    // A site of its own per test: a site has one connection per product, and every host the fake
    // answers for resolves to this one cloud id.
    private readonly string _cloudId = Guid.NewGuid().ToString("D");
    private string Root => "/ex/confluence/" + _cloudId + "/wiki";
    private const string Secret = "atlassian-secret-value-123";
    private const string EditorEmail = "editor@atlassian-sites-tests.connapse.io";
    private const string EditorPassword = "EditorTest1!";

    private const string Sites = "/api/v1/atlassian/sites";

    private readonly SharedWebAppFixture _fixture;
    private readonly FakeAtlassianApi _api;

    public AtlassianSitesEndpointTests(SharedWebAppFixture fixture)
    {
        _fixture = fixture;
        _api = fixture.Atlassian;
    }

    public async Task InitializeAsync()
    {
        // The fake is shared across the collection: put it in a known state.
        _api.FailTokenWith(null);
        _api.AnswerTokenWith(null);
        _api.FailWith(null);
        _api.MapJson(FakeAtlassianApi.TenantInfoPath, new { cloudId = _cloudId });
        _api.MapJson(Root + "/rest/api/user/current", new { accountId = "svc" });
        _api.MapJson(Root + "/api/v2/spaces", new { results = new[] { new { id = "1" } } });
        _api.MapJson(Root + "/api/v2/pages", new { results = new[] { new { id = "7" } } });
        _api.MapJson(Root + "/rest/api/search/user", new
        {
            results = new object[] { new { user = new { accountId = "svc" } }, new { user = new { accountId = "other" } } },
        });
        _api.MapJson(Root + "/rest/api/content/7/permission/check", new { hasPermission = true });

        using var scope = _fixture.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();
        if (await users.FindByEmailAsync(EditorEmail) is null)
        {
            var user = new ConnapseUser
            {
                UserName = EditorEmail,
                Email = EditorEmail,
                EmailConfirmed = true,
                DisplayName = EditorEmail,
                CreatedAt = DateTime.UtcNow,
            };
            (await users.CreateAsync(user, EditorPassword)).Succeeded.Should().BeTrue();
            await users.AddToRoleAsync(user, "Editor");
        }
    }

    // The fake is shared across the collection: a refused token left behind would fail other classes' sign-ins and checks.
    public Task DisposeAsync()
    {
        _api.FailTokenWith(null);
        _api.AnswerTokenWith(null);
        _api.FailWith(null);
        return Task.CompletedTask;
    }

    private async Task<HttpClient> EditorClientAsync()
    {
        using var anon = _fixture.Factory.CreateClient();
        var response = await anon.PostAsJsonAsync("/api/v1/auth/token", new { email = EditorEmail, password = EditorPassword });
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<JsonElement>();

        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.GetProperty("accessToken").GetString());
        return client;
    }

    private Task<HttpResponseMessage> CreateAsync(HttpClient client, string host, string secret = Secret, string? product = null) =>
        client.PostAsJsonAsync(Sites, new { siteUrl = $"https://{host}", clientId = "client-1", clientSecret = secret, product });

    private async Task<Connection?> FindAsync(string name)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var all = await scope.ServiceProvider.GetRequiredService<IConnectionStore>().ListAsync(0, 1000);
        return all.FirstOrDefault(c => c.Name == name);
    }

    [Fact]
    public async Task Create_NonAdmin_Returns403AndCreatesNothing()
    {
        using var editor = await EditorClientAsync();

        var response = await CreateAsync(editor, "nonadmin.atlassian.net");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await FindAsync("nonadmin.atlassian.net")).Should().BeNull();
    }

    [Fact]
    public async Task Resolve_NonAdmin_Returns403()
    {
        using var editor = await EditorClientAsync();

        var response = await editor.PostAsJsonAsync($"{Sites}/resolve", new { siteUrl = "https://acme.atlassian.net" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Create_Anonymous_Returns401()
    {
        using var anon = _fixture.Factory.CreateClient();

        var response = await CreateAsync(anon, "anon.atlassian.net");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Resolve_AtlassianSite_ReturnsCloudId()
    {
        var response = await _fixture.AdminClient.PostAsJsonAsync($"{Sites}/resolve", new { siteUrl = "acme.atlassian.net" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("cloudId").GetString().Should().Be(_cloudId);
        body.GetProperty("siteUrl").GetString().Should().Be("https://acme.atlassian.net");
    }

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("http://169.254.169.254")]
    [InlineData("https://acme.atlassian.net.evil.com")]
    public async Task Resolve_HostThatIsNotAtlassianNet_Returns400WithoutARequest(string siteUrl)
    {
        int before = _api.Requests.Count;

        var response = await _fixture.AdminClient.PostAsJsonAsync($"{Sites}/resolve", new { siteUrl });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _api.Requests.Count.Should().Be(before);
    }

    [Fact]
    public async Task Create_FailingTester_Returns422NamingTheStepAndCreatesNoConnection()
    {
        _api.FailTokenWith(HttpStatusCode.Unauthorized);

        var response = await CreateAsync(_fixture.AdminClient, "failing.atlassian.net");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("step").GetString().Should().Be("token");
        (await response.Content.ReadAsStringAsync()).Should().NotContain(Secret);
        (await FindAsync("failing.atlassian.net")).Should().BeNull();
    }

    [Fact]
    public async Task Create_FailingTester_ReportsThePlainStepAndTroubleshootingLink()
    {
        _api.FailTokenWith(HttpStatusCode.Unauthorized);

        var response = await CreateAsync(_fixture.AdminClient, "failing-labels.atlassian.net");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("stepLabel").GetString().Should().Be("Signing in as the service account");
        body.GetProperty("troubleshootingUrl").GetString()
            .Should().Be("https://github.com/Destrayon/Connapse/blob/main/docs/sources/atlassian-setup.md#client-id-or-secret-rejected");
        body.GetProperty("detail").GetString().Should().Contain("HTTP 401");
    }

    [Fact]
    public async Task Create_SiteWithNoPages_Returns422WarningUnlessSaveAnyway()
    {
        _api.MapJson(Root + "/api/v2/pages", new { results = Array.Empty<object>() });

        var warned = await CreateAsync(_fixture.AdminClient, "no-pages.atlassian.net");
        warned.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await warned.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("warning").GetBoolean().Should().BeTrue();
        (await FindAsync("no-pages.atlassian.net")).Should().BeNull();

        var saved = await _fixture.AdminClient.PostAsJsonAsync(Sites,
            new { siteUrl = "https://no-pages.atlassian.net", clientId = "client-1", clientSecret = Secret, saveAnyway = true });

        saved.StatusCode.Should().Be(HttpStatusCode.Created);
        var connection = await FindAsync("no-pages.atlassian.net");
        connection!.Health!.Warning.Should().BeTrue();
        connection.Health.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Get_ReturnsTheSiteItsHealthAndTheScopesItNeeds_WithoutTheSecret()
    {
        Guid id = await CreateSiteAsync("detail.atlassian.net");

        var response = await _fixture.AdminClient.GetAsync($"{Sites}/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(Secret);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("siteUrl").GetString().Should().Be("https://detail.atlassian.net");
        body.GetProperty("health").GetProperty("succeeded").GetBoolean().Should().BeTrue();
        body.GetProperty("health").GetProperty("lastSucceededAt").ValueKind.Should().Be(JsonValueKind.String);
        body.GetProperty("product").GetString().Should().Be("confluence");
        body.GetProperty("scopes").EnumerateArray().Select(s => s.GetProperty("name").GetString())
            .Should().Equal(Connapse.Storage.ConnectionTesters.AtlassianSiteScopes.Confluence.Select(s => s.Name),
                "a Confluence connection's credential needs Confluence scopes only");
    }

    [Fact]
    public async Task Get_JiraConnection_ListsOnlyTheJiraScopes()
    {
        Guid id = await CreateSiteAsync("detail-jira.atlassian.net", product: "jira");

        var body = await (await _fixture.AdminClient.GetAsync($"{Sites}/{id}")).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("product").GetString().Should().Be("jira");
        body.GetProperty("scopes").EnumerateArray().Select(s => s.GetProperty("name").GetString())
            .Should().Equal(Connapse.Storage.ConnectionTesters.AtlassianSiteScopes.Jira.Select(s => s.Name));
        body.GetProperty("scopes").EnumerateArray().Should().OnlyContain(s => s.GetProperty("product").GetString() == "jira");
    }

    [Fact]
    public async Task Test_RunsTheProbesWithTheStoredSecretAndRecordsTheResult()
    {
        Guid id = await CreateSiteAsync("retest.atlassian.net");
        _api.FailTokenWith(HttpStatusCode.Unauthorized);
        try
        {
            var response = await _fixture.AdminClient.PostAsync($"{Sites}/{id}/test", null);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("success").GetBoolean().Should().BeFalse();
            body.GetProperty("step").GetString().Should().Be("token");
        }
        finally
        {
            _api.FailTokenWith(null);
        }

        var connection = await FindAsync("retest.atlassian.net");
        connection!.Health!.CredentialRejected.Should().BeTrue();
        connection.Health.LastSucceededAt.Should().NotBeNull("the save before it passed");
    }

    [Fact]
    public async Task Test_NonAdmin_Returns403()
    {
        using var editor = await EditorClientAsync();

        (await editor.PostAsync($"{Sites}/{Guid.NewGuid()}/test", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await editor.PutAsJsonAsync($"{Sites}/{Guid.NewGuid()}/credentials", new { clientId = "c", clientSecret = "s" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await editor.GetAsync($"{Sites}/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ReplaceCredentials_Passing_UpdatesInPlaceAndSourcesKeepTheirConnection()
    {
        Guid id = await CreateSiteAsync("replace.atlassian.net");
        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISourceStore>().CreateAsync(new CreateSourceRequest(
                $"replace-src-{Guid.NewGuid():N}"[..24], id, """{"kind":"confluence-space","spaceId":"1","spaceKey":"ENG"}"""));
        }

        var response = await _fixture.AdminClient.PutAsJsonAsync($"{Sites}/{id}/credentials",
            new { clientId = "client-2", clientSecret = "rotated-secret-456" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("rotated-secret-456");
        using var check = _fixture.Factory.Services.CreateScope();
        var store = check.ServiceProvider.GetRequiredService<IConnectionStore>();
        var connection = await store.GetAsync(id);
        connection!.SourceCount.Should().Be(1);
        AtlassianSite.FromConfigJson(connection.ConfigJson)!.ClientId.Should().Be("client-2");
        (await store.GetSecretAsync(id)).Should().Be("rotated-secret-456");
    }

    [Fact]
    public async Task ReplaceCredentials_Failing_Returns422AndKeepsTheOldCredential()
    {
        Guid id = await CreateSiteAsync("replace-failing.atlassian.net");
        _api.FailTokenWith(HttpStatusCode.Unauthorized);
        try
        {
            var response = await _fixture.AdminClient.PutAsJsonAsync($"{Sites}/{id}/credentials",
                new { clientId = "client-2", clientSecret = "wrong-secret" });

            response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("step").GetString().Should().Be("token");
        }
        finally
        {
            _api.FailTokenWith(null);
        }

        using var scope = _fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        (await store.GetSecretAsync(id)).Should().Be(Secret);
        AtlassianSite.FromConfigJson((await store.GetAsync(id))!.ConfigJson)!.ClientId.Should().Be("client-1");
    }

    [Fact]
    public async Task ReplaceCredentials_MissingFields_Returns400()
    {
        Guid id = await CreateSiteAsync("replace-missing.atlassian.net");

        var response = await _fixture.AdminClient.PutAsJsonAsync($"{Sites}/{id}/credentials", new { clientId = "client-2" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_MalformedTokenResponse_Returns422AtTokenStepAndCreatesNoConnection()
    {
        _api.AnswerTokenWith("{not json");
        try
        {
            var response = await CreateAsync(_fixture.AdminClient, "malformed-token.atlassian.net");

            response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("step").GetString().Should().Be("token");
            (await FindAsync("malformed-token.atlassian.net")).Should().BeNull();
        }
        finally
        {
            _api.AnswerTokenWith(null);
        }
    }

    [Fact]
    public async Task Create_PassingTester_CreatesAnAtlassianConnectionWithAnEncryptedSecret()
    {
        var response = await CreateAsync(_fixture.AdminClient, "passing.atlassian.net");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        string raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(Secret);
        // A Confluence connection's report names Confluence alone (#725).
        JsonDocument.Parse(raw).RootElement.GetProperty("test").TryGetProperty("otherProducts", out _).Should().BeFalse();

        var connection = await FindAsync("passing.atlassian.net");
        connection.Should().NotBeNull();
        connection!.Provider.Should().Be(ConnectionProvider.Atlassian);
        connection.HasSecret.Should().BeTrue();

        var site = AtlassianSite.FromConfigJson(connection.ConfigJson);
        site.Should().Be(new AtlassianSite("https://passing.atlassian.net", _cloudId, "client-1"));

        using var scope = _fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        (await store.GetSecretAsync(connection.Id)).Should().Be(Secret);
        connection.ConfigJson.Should().NotContain(Secret);
    }

    [Fact]
    public async Task Create_SameSiteTwice_Returns409()
    {
        (await CreateAsync(_fixture.AdminClient, "twice.atlassian.net")).StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await CreateAsync(_fixture.AdminClient, "twice.atlassian.net");

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    private async Task<Guid> CreateSiteAsync(string host, string? product = null)
    {
        // A Jira connection's test reads Jira, so the fake site answers as a working Jira.
        if (product == "jira")
            new FakeJira(_api, _cloudId).MapSiteTest();
        var response = await CreateAsync(_fixture.AdminClient, host, product: product);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    private async Task<Guid> AddSourceAsync(Guid connectionId, string name)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var source = await scope.ServiceProvider.GetRequiredService<ISourceStore>().CreateAsync(new CreateSourceRequest(
            name, connectionId, """{"kind":"confluence-space","spaceId":"1","spaceKey":"ENG"}"""));
        return source.Id;
    }

    private async Task<List<(string Action, string Details)>> AuditFor(Guid connectionId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        await using var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ConnapseIdentityDbContext>>().CreateDbContextAsync();
        string id = connectionId.ToString();
        var rows = await db.AuditLogs.Where(a => a.ResourceId == id).OrderBy(a => a.CreatedAt).ToListAsync();
        return rows.Select(r => (r.Action, r.Details?.RootElement.GetRawText() ?? "")).ToList();
    }

    [Fact]
    public async Task NewRoutes_NonAdmin_Return403()
    {
        using var editor = await EditorClientAsync();
        Guid id = Guid.NewGuid();

        (await editor.GetAsync(Sites)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await editor.PatchAsJsonAsync($"{Sites}/{id}", new { name = "x" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await editor.PostAsync($"{Sites}/{id}/pause", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await editor.PostAsJsonAsync($"{Sites}/{id}/resume", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await editor.DeleteAsync($"{Sites}/{id}?confirmName=x")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task List_ReturnsOnlyAtlassianSites_WithoutTheSecret()
    {
        Guid id = await CreateSiteAsync("listed.atlassian.net");
        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IConnectionStore>().CreateAsync(
                new CreateConnectionRequest($"list-not-atlassian-{Guid.NewGuid():N}"[..28], ConnectionProvider.Filesystem,
                    "{\"allowedRoots\":[\"/tmp\"]}", null), null);
        }

        var response = await _fixture.AdminClient.GetAsync(Sites);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(Secret);
        var sites = JsonDocument.Parse(raw).RootElement.EnumerateArray().ToList();
        sites.Should().NotContain(s => s.GetProperty("name").GetString()!.StartsWith("list-not-atlassian"));
        var listed = sites.Single(s => s.GetProperty("id").GetGuid() == id);
        listed.GetProperty("siteUrl").GetString().Should().Be("https://listed.atlassian.net");
        listed.GetProperty("syncPaused").GetBoolean().Should().BeFalse();
        listed.TryGetProperty("scopes", out _).Should().BeFalse("only a single site carries its scopes");
    }

    [Fact]
    public async Task Rename_RenamesTheSiteAndAuditsIt()
    {
        Guid id = await CreateSiteAsync("rename.atlassian.net");

        var response = await _fixture.AdminClient.PatchAsJsonAsync($"{Sites}/{id}", new { name = "Rename wiki" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString().Should().Be("Rename wiki");
        (await FindAsync("Rename wiki")).Should().NotBeNull();
        (await AuditFor(id)).Should().Contain(e => e.Action == "connection.updated"
            && e.Details.Contains("rename.atlassian.net") && e.Details.Contains("Rename wiki"));
    }

    [Fact]
    public async Task Rename_BlankOrTakenName_IsRefused()
    {
        Guid id = await CreateSiteAsync("rename-refused.atlassian.net");
        // The site's other connection: the test site has one cloud id, and one connection per product.
        await CreateSiteAsync("rename-taken.atlassian.net", product: "jira");

        (await _fixture.AdminClient.PatchAsJsonAsync($"{Sites}/{id}", new { name = " " }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _fixture.AdminClient.PatchAsJsonAsync($"{Sites}/{id}", new { name = "rename-taken.atlassian.net (Jira)" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _fixture.AdminClient.PatchAsJsonAsync($"{Sites}/{Guid.NewGuid()}", new { name = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PauseAndResume_DisableAndReEnableTheSitesSources()
    {
        Guid id = await CreateSiteAsync("pause.atlassian.net");
        Guid first = await AddSourceAsync(id, $"pause-a-{Guid.NewGuid():N}"[..20]);
        Guid second = await AddSourceAsync(id, $"pause-b-{Guid.NewGuid():N}"[..20]);

        var paused = await _fixture.AdminClient.PostAsync($"{Sites}/{id}/pause", null);
        paused.StatusCode.Should().Be(HttpStatusCode.OK);
        (await paused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sources").GetInt32().Should().Be(2);
        (await (await _fixture.AdminClient.GetAsync($"{Sites}/{id}")).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("syncPaused").GetBoolean().Should().BeTrue();

        (await _fixture.AdminClient.PostAsJsonAsync($"{Sites}/{id}/resume", new { sourceIds = new[] { Guid.NewGuid() } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "a source that isn't on the site");

        var resumed = await _fixture.AdminClient.PostAsJsonAsync($"{Sites}/{id}/resume", new { sourceIds = new[] { first } });
        resumed.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _fixture.Factory.Services.CreateScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        (await sources.GetAsync(first))!.Enabled.Should().BeTrue();
        (await sources.GetAsync(second))!.Enabled.Should().BeFalse("only the chosen source was turned back on");
        (await AuditFor(id)).Select(e => e.Action).Should().Contain(["connection.paused", "connection.resumed"]);
    }

    [Fact]
    public async Task Pause_SiteWithoutSources_Returns409()
    {
        Guid id = await CreateSiteAsync("pause-empty.atlassian.net");

        (await _fixture.AdminClient.PostAsync($"{Sites}/{id}/pause", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Delete_NeedsTheNameTyped_IsRefusedWhileSourcesUseIt_ThenDeletesAndAudits()
    {
        Guid id = await CreateSiteAsync("delete.atlassian.net");
        string sourceName = $"delete-src-{Guid.NewGuid():N}"[..24];
        Guid source = await AddSourceAsync(id, sourceName);

        (await _fixture.AdminClient.DeleteAsync($"{Sites}/{id}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _fixture.AdminClient.DeleteAsync($"{Sites}/{id}?confirmName=delete")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var refused = await _fixture.AdminClient.DeleteAsync($"{Sites}/{id}?confirmName=delete.atlassian.net");
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("sources")[0].GetProperty("name").GetString().Should().Be(sourceName);
        body.GetProperty("sources")[0].GetProperty("url").GetString().Should().Be($"/sources/{source}");
        body.GetProperty("error").GetString().Should().Be(
            $"'delete.atlassian.net' can't be deleted while 1 source uses it: {sourceName}. Delete that source first.",
            "the API names the sources, as the page's dialog does");
        (await FindAsync("delete.atlassian.net")).Should().NotBeNull();

        using (var scope = _fixture.Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ISourceStore>().DeleteAsync(source);

        (await _fixture.AdminClient.DeleteAsync($"{Sites}/{id}?confirmName=delete.atlassian.net"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await FindAsync("delete.atlassian.net")).Should().BeNull();
        (await AuditFor(id)).Should().Contain(e => e.Action == "connection.deleted");
    }

    [Fact]
    public async Task Delete_ConnectionThatIsNotAtlassian_Returns404()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var other = await scope.ServiceProvider.GetRequiredService<IConnectionStore>().CreateAsync(
            new CreateConnectionRequest("delete-not-atlassian", ConnectionProvider.Filesystem, "{\"allowedRoots\":[\"/tmp\"]}", null), null);

        (await _fixture.AdminClient.DeleteAsync($"{Sites}/{other.Id}?confirmName=delete-not-atlassian"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private void MapSpaces() => _api.MapJson(Root + "/api/v2/spaces", new
    {
        results = new object[]
        {
            new { id = "100", key = "ENG", name = "Engineering", type = "global" },
            new { id = "300", key = "~pat", name = "Pat's notes", type = "personal" },
            new { id = "200", key = "HB", name = "Handbook", type = "global" },
        },
    });

    [Fact]
    public async Task Spaces_NonAdmin_Returns403()
    {
        using var editor = await EditorClientAsync();

        var response = await editor.GetAsync($"{Sites}/{Guid.NewGuid()}/spaces");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Spaces_Anonymous_Returns401()
    {
        using var anon = _fixture.Factory.CreateClient();

        var response = await anon.GetAsync($"{Sites}/{Guid.NewGuid()}/spaces");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Spaces_UnknownConnection_Returns404()
    {
        var response = await _fixture.AdminClient.GetAsync($"{Sites}/{Guid.NewGuid()}/spaces");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Spaces_ConnectionThatIsNotAtlassian_Returns400()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var other = await scope.ServiceProvider.GetRequiredService<IConnectionStore>().CreateAsync(
            new CreateConnectionRequest("spaces-not-atlassian", ConnectionProvider.Filesystem,
                "{\"allowedRoots\":[\"/tmp\"]}", null), null);

        var response = await _fixture.AdminClient.GetAsync($"{Sites}/{other.Id}/spaces");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SpacesAndProjects_OtherProductsConnection_Return400()
    {
        Guid confluence = await CreateSiteAsync("list-wrong-product.atlassian.net");
        Guid jira = await CreateSiteAsync("list-wrong-product.atlassian.net", product: "jira");

        var spaces = await _fixture.AdminClient.GetAsync($"{Sites}/{jira}/spaces");
        var projects = await _fixture.AdminClient.GetAsync($"{Sites}/{confluence}/projects");

        spaces.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await spaces.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .Should().Be("This is a Jira connection; list its projects instead.");
        projects.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await projects.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .Should().Be("This is a Confluence connection; list its spaces instead.");
    }

    [Fact]
    public async Task Spaces_Admin_ListsSpacesWithoutPersonalOnesByDefault()
    {
        Guid id = await CreateSiteAsync("spaces-default.atlassian.net");
        MapSpaces();

        var response = await _fixture.AdminClient.GetAsync($"{Sites}/{id}/spaces");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(Secret);
        var body = JsonDocument.Parse(raw).RootElement;
        body.EnumerateArray().Select(e => e.GetProperty("key").GetString()).Should().Equal("ENG", "HB");
        body[0].GetProperty("id").GetString().Should().Be("100");
        body[0].GetProperty("name").GetString().Should().Be("Engineering");
        body[0].GetProperty("type").GetString().Should().Be("global");
    }

    [Fact]
    public async Task Spaces_IncludePersonal_ListsPersonalSpacesToo()
    {
        Guid id = await CreateSiteAsync("spaces-personal.atlassian.net");
        MapSpaces();

        var response = await _fixture.AdminClient.GetAsync($"{Sites}/{id}/spaces?includePersonal=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.EnumerateArray().Select(e => e.GetProperty("key").GetString()).Should().BeEquivalentTo("ENG", "HB", "~pat");
    }

    [Fact]
    public async Task Spaces_AtlassianFails_Returns502WithoutTheSecret()
    {
        Guid id = await CreateSiteAsync("spaces-failing.atlassian.net");
        _api.FailWith(HttpStatusCode.InternalServerError);

        var response = await _fixture.AdminClient.GetAsync($"{Sites}/{id}/spaces");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(Secret).And.Contain("error");
    }
}
