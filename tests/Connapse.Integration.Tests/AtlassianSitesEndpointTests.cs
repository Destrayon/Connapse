using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Connectors;
using Connapse.Identity.Data.Entities;
using Connapse.Storage.Connectors.Atlassian;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
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
    private const string CloudId = "11111111-2222-3333-4444-555555555555";
    private const string Root = "/ex/confluence/" + CloudId + "/wiki";
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
        _api.MapJson(FakeAtlassianApi.TenantInfoPath, new { cloudId = CloudId });
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
                UserName = EditorEmail, Email = EditorEmail, EmailConfirmed = true,
                DisplayName = EditorEmail, CreatedAt = DateTime.UtcNow,
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

    private Task<HttpResponseMessage> CreateAsync(HttpClient client, string host, string secret = Secret) =>
        client.PostAsJsonAsync(Sites, new { siteUrl = $"https://{host}", clientId = "client-1", clientSecret = secret });

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
        body.GetProperty("cloudId").GetString().Should().Be(CloudId);
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
            .Should().Be("https://github.com/Destrayon/Connapse/blob/main/docs/atlassian-setup.md#client-id-or-secret-rejected");
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
        body.GetProperty("scopes").GetArrayLength().Should().Be(7);
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

        var connection = await FindAsync("passing.atlassian.net");
        connection.Should().NotBeNull();
        connection!.Provider.Should().Be(ConnectionProvider.Atlassian);
        connection.HasSecret.Should().BeTrue();

        var site = AtlassianSite.FromConfigJson(connection.ConfigJson);
        site.Should().Be(new AtlassianSite("https://passing.atlassian.net", CloudId, "client-1"));

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

    private async Task<Guid> CreateSiteAsync(string host)
    {
        var response = await CreateAsync(_fixture.AdminClient, host);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
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
