using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Connectors;
using Connapse.Identity.Data.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// <c>POST</c> and <c>PATCH /api/sources</c> with a <c>jira-project</c> scope, and the project
/// listing: the checks the New source dialog runs for Jira. The site must have passed for Jira,
/// the project must be one the service account can browse now and not already a source.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class JiraSourcesEndpointTests(SharedWebAppFixture fixture) : IAsyncLifetime
{
    // A cloud id of its own per test: the Jira routes mapped here never make other classes' sites pass
    // for Jira, and a site has one connection per product while every host the fake answers for
    // resolves to this one cloud id.
    private readonly string _cloudId = Guid.NewGuid().ToString("D");
    private string Wiki => "/ex/confluence/" + _cloudId + "/wiki";
    private const string Secret = "jira-sources-secret-123";
    private const string EditorEmail = "editor@jira-sources-tests.connapse.io";
    private const string EditorPassword = "EditorTest1!";

    private readonly FakeAtlassianApi _api = fixture.Atlassian;
    private FakeJira _jira = null!;

    public async Task InitializeAsync()
    {
        // The fake is shared across the collection: put it in a known state.
        _api.FailTokenWith(null);
        _api.AnswerTokenWith(null);
        _api.FailWith(null);
        _api.MapJson(FakeAtlassianApi.TenantInfoPath, new { cloudId = _cloudId });
        _api.MapJson(Wiki + "/rest/api/user/current", new { accountId = "svc" });
        _api.MapJson(Wiki + "/api/v2/spaces", new { results = new[] { new { id = "100", key = "ENG", name = "Engineering", type = "global" } } });
        _api.MapJson(Wiki + "/api/v2/pages", new { results = new[] { new { id = "7" } } });
        _api.MapJson(Wiki + "/rest/api/search/user", new
        {
            results = new object[] { new { user = new { accountId = "svc" } }, new { user = new { accountId = "other" } } },
        });
        _api.MapJson(Wiki + "/rest/api/content/7/permission/check", new { hasPermission = true });

        _jira = new FakeJira(_api, _cloudId).MapSiteTest();
        _api.MapJson(_jira.Root + "/project/search", new
        {
            values = new object[]
            {
                new { id = "10000", key = "ENG", name = "Engineering", projectTypeKey = "software", style = "classic" },
                new { id = "10001", key = "OPS", name = "Operations", projectTypeKey = "service_desk", style = "next-gen" },
            },
            startAt = 0, maxResults = 100, total = 2, isLast = true,
        });

        using var scope = fixture.Factory.Services.CreateScope();
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

    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient Admin => fixture.AdminClient;

    private async Task<Guid> CreateSiteAsync(string host, string product = "jira")
    {
        var response = await Admin.PostAsJsonAsync("/api/v1/atlassian/sites",
            new { siteUrl = $"https://{host}", clientId = "client-1", clientSecret = Secret, product });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> CreateSourceAsync(Guid connectionId, object scope, string? name = null) =>
        Admin.PostAsJsonAsync("/api/sources", new { name, connectionId, scopeJson = JsonSerializer.Serialize(scope) });

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<string> StoredScopeAsync(Guid sourceId)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var source = await scope.ServiceProvider.GetRequiredService<ISourceStore>().GetAsync(sourceId);
        return source!.ScopeJson;
    }

    private async Task<int> SourceCountAsync(Guid connectionId)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<ISourceStore>().ListByConnectionAsync(connectionId)).Count;
    }

    private async Task<HttpClient> EditorClientAsync()
    {
        using var anon = fixture.Factory.CreateClient();
        var response = await anon.PostAsJsonAsync("/api/v1/auth/token", new { email = EditorEmail, password = EditorPassword });
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<JsonElement>();

        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.GetProperty("accessToken").GetString());
        return client;
    }

    [Fact]
    public async Task Create_JiraProject_ScopeRebuiltFromAtlassian()
    {
        Guid site = await CreateSiteAsync("jira-api-create.atlassian.net");

        var response = await CreateSourceAsync(site,
            new { kind = "jira-project", projectId = "10000", projectKey = "WRONG", includeAttachments = true, maxAttachmentMb = 40 });

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await BodyAsync(response);
        body.GetProperty("name").GetString().Should().Be("jira-api-create.atlassian.net / Engineering (Jira)");

        using var scope = JsonDocument.Parse(await StoredScopeAsync(body.GetProperty("id").GetGuid()));
        scope.RootElement.GetProperty("kind").GetString().Should().Be("jira-project");
        scope.RootElement.GetProperty("projectId").GetString().Should().Be("10000");
        scope.RootElement.GetProperty("projectKey").GetString().Should().Be("ENG");
        scope.RootElement.GetProperty("projectName").GetString().Should().Be("Engineering");
        scope.RootElement.GetProperty("includeAttachments").GetBoolean().Should().BeTrue();
        scope.RootElement.GetProperty("maxAttachmentMb").GetInt32().Should().Be(40);

        var read = (await BodyAsync(await Admin.GetAsync($"/api/sources/{body.GetProperty("id").GetGuid()}"))).GetProperty("jira");
        read.GetProperty("projectKey").GetString().Should().Be("ENG");
        read.GetProperty("maxAttachmentMb").GetInt32().Should().Be(40);
    }

    [Fact]
    public async Task Create_ProjectNotBrowsable_400()
    {
        Guid site = await CreateSiteAsync("jira-api-unbrowsable.atlassian.net");

        var response = await CreateSourceAsync(site, new { kind = "jira-project", projectId = "99999" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).GetProperty("field").GetString().Should().Be("projectId");
        (await SourceCountAsync(site)).Should().Be(0);
    }

    [Fact]
    public async Task Create_DuplicateProject_409()
    {
        Guid site = await CreateSiteAsync("jira-api-twice.atlassian.net");
        (await CreateSourceAsync(site, new { kind = "jira-project", projectId = "10000" })).StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await CreateSourceAsync(site, new { kind = "jira-project", projectId = "10000" }, name: "another-name");

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await BodyAsync(second)).GetProperty("field").GetString().Should().Be("projectId");
        (await SourceCountAsync(site)).Should().Be(1);
    }

    [Fact]
    public async Task Create_JiraConnectionWhoseLastTestFailed_400()
    {
        // Jira refuses the service account, so the Jira connection isn't saved at all.
        _jira.Answer("/myself", HttpStatusCode.Forbidden);
        var refused = await Admin.PostAsJsonAsync("/api/v1/atlassian/sites",
            new { siteUrl = "https://jira-api-nojira.atlassian.net", clientId = "client-1", clientSecret = Secret, product = "jira" });
        refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await BodyAsync(refused)).GetProperty("step").GetString().Should().Be("jira-identity");

        // A Jira connection saved earlier whose last test then failed is offered no Jira source.
        Guid site;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
            var connection = await store.CreateAsync(new Connapse.Core.CreateConnectionRequest(
                "jira-api-failed.atlassian.net", Connapse.Core.ConnectionProvider.Atlassian,
                $$"""{"siteUrl":"https://jira-api-failed.atlassian.net","cloudId":"{{_cloudId}}","clientId":"client-1","product":"jira"}""",
                Secret), null);
            await store.RecordHealthAsync(connection.Id, new Connapse.Core.ConnectionHealth(
                DateTime.UtcNow, Succeeded: false, Step: "jira-admin", Message: "m", Origin: "test"));
            site = connection.Id;
        }

        var response = await CreateSourceAsync(site, new { kind = "jira-project", projectId = "10000" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).GetProperty("error").GetString()
            .Should().Be("Jira sources can be added once this site passes a test. Choose Test connection on the Connections page.");
        (await SourceCountAsync(site)).Should().Be(0);

        // A scope without a kind is still Confluence's, as before Jira, so this Jira connection refuses it.
        var space = await CreateSourceAsync(site, new { spaceId = "100" });
        space.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(space)).GetProperty("field").GetString().Should().Be("connectionId");
    }

    [Fact]
    public async Task Create_JiraSourceOnConfluenceConnection_400()
    {
        Guid confluence = await CreateSiteAsync("jira-api-wrongproduct.atlassian.net", product: "confluence");

        var response = await CreateSourceAsync(confluence, new { kind = "jira-project", projectId = "10000" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await BodyAsync(response);
        body.GetProperty("field").GetString().Should().Be("connectionId");
        body.GetProperty("error").GetString().Should().Be(
            "Connection 'jira-api-wrongproduct.atlassian.net' is a Confluence connection, so it can't hold a Jira source. "
            + "Choose this site's Jira connection, or add one on the Connections page. "
            + "A Confluence source's scope names \"spaceId\", as in {\"spaceId\":\"98306\"}.");
        (await SourceCountAsync(confluence)).Should().Be(0);

        // The site's own Jira connection, added beside it, takes the project.
        Guid jira = await CreateSiteAsync("jira-api-wrongproduct.atlassian.net");
        jira.Should().NotBe(confluence);
        (await CreateSourceAsync(jira, new { kind = "jira-project", projectId = "10000" })).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task OneSite_AddedAsBothConnections_EachHoldsItsOwnProductsSource()
    {
        const string host = "jira-api-both.atlassian.net";
        Guid confluence = await CreateSiteAsync(host, product: "confluence");
        Guid jira = await CreateSiteAsync(host);

        var sites = await BodyAsync(await Admin.GetAsync("/api/v1/atlassian/sites"));
        sites.EnumerateArray()
            .Where(s => s.GetProperty("id").GetGuid() == confluence || s.GetProperty("id").GetGuid() == jira)
            .Select(s => (s.GetProperty("name").GetString(), s.GetProperty("product").GetString()))
            .Should().BeEquivalentTo([(host, "confluence"), ($"{host} (Jira)", "jira")]);

        var space = await CreateSourceAsync(confluence, new { kind = "confluence-space", spaceId = "100" });
        space.StatusCode.Should().Be(HttpStatusCode.Created, await space.Content.ReadAsStringAsync());
        // The space and the project are both Engineering: the Jira source's name says Jira, so they don't collide.
        var project = await CreateSourceAsync(jira, new { kind = "jira-project", projectId = "10000" });
        project.StatusCode.Should().Be(HttpStatusCode.Created, await project.Content.ReadAsStringAsync());
        var spaceBody = await BodyAsync(space);
        var projectBody = await BodyAsync(project);
        spaceBody.GetProperty("name").GetString().Should().Be($"{host} / Engineering");
        projectBody.GetProperty("name").GetString().Should().Be($"{host} / Engineering (Jira)");

        using (var stored = JsonDocument.Parse(await StoredScopeAsync(spaceBody.GetProperty("id").GetGuid())))
            stored.RootElement.GetProperty("kind").GetString().Should().Be("confluence-space");
        using (var stored = JsonDocument.Parse(await StoredScopeAsync(projectBody.GetProperty("id").GetGuid())))
            stored.RootElement.GetProperty("kind").GetString().Should().Be("jira-project");
        (await SourceCountAsync(confluence)).Should().Be(1);
        (await SourceCountAsync(jira)).Should().Be(1);
    }

    [Fact]
    public async Task Patch_ProjectIdChange_Refused()
    {
        Guid site = await CreateSiteAsync("jira-api-patch.atlassian.net");
        var created = await CreateSourceAsync(site, new { kind = "jira-project", projectId = "10000" });
        Guid sourceId = (await BodyAsync(created)).GetProperty("id").GetGuid();

        var moved = await Admin.PatchAsJsonAsync($"/api/sources/{sourceId}",
            new { scopeJson = JsonSerializer.Serialize(new { projectId = "10001" }) });
        moved.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(moved)).GetProperty("field").GetString().Should().Be("projectId");

        var ok = await Admin.PatchAsJsonAsync($"/api/sources/{sourceId}",
            new { scopeJson = JsonSerializer.Serialize(new { includeAttachments = false, maxAttachmentMb = 10 }) });
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());

        using var scope = JsonDocument.Parse(await StoredScopeAsync(sourceId));
        scope.RootElement.GetProperty("kind").GetString().Should().Be("jira-project");
        scope.RootElement.GetProperty("projectId").GetString().Should().Be("10000");
        scope.RootElement.GetProperty("includeAttachments").GetBoolean().Should().BeFalse();
        scope.RootElement.GetProperty("maxAttachmentMb").GetInt32().Should().Be(10);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Patch_EmptyScopeJson_RefusedAndScopeKept(string empty)
    {
        Guid site = await CreateSiteAsync($"jira-api-empty-{empty.Length}.atlassian.net");
        var created = await CreateSourceAsync(site, new { kind = "jira-project", projectId = "10000" });
        Guid sourceId = (await BodyAsync(created)).GetProperty("id").GetGuid();
        string before = await StoredScopeAsync(sourceId);

        var patched = await Admin.PatchAsJsonAsync($"/api/sources/{sourceId}", new { scopeJson = empty });

        patched.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await BodyAsync(patched);
        body.GetProperty("field").GetString().Should().Be("scopeJson");
        body.GetProperty("error").GetString().Should().Be(Connapse.Web.Services.AtlassianSourceScope.EmptyScopeError);
        (await StoredScopeAsync(sourceId)).Should().Be(before);
    }

    [Fact]
    public async Task Projects_NonAdmin_403()
    {
        Guid site = await CreateSiteAsync("jira-api-list.atlassian.net");
        using var editor = await EditorClientAsync();

        (await editor.GetAsync($"/api/v1/atlassian/sites/{site}/projects")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var listed = await Admin.GetAsync($"/api/v1/atlassian/sites/{site}/projects");
        listed.StatusCode.Should().Be(HttpStatusCode.OK, await listed.Content.ReadAsStringAsync());
        var projects = await BodyAsync(listed);
        projects.EnumerateArray().Select(p => p.GetProperty("key").GetString()).Should().Equal("ENG", "OPS");
        (await listed.Content.ReadAsStringAsync()).Should().NotContain(Secret);
    }
}
