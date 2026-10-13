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
    // A cloud id of its own, so the Jira routes mapped here never make other classes' sites pass for Jira.
    private const string CloudId = "22222222-3333-4444-5555-666666666666";
    private const string Wiki = "/ex/confluence/" + CloudId + "/wiki";
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
        _api.MapJson(FakeAtlassianApi.TenantInfoPath, new { cloudId = CloudId });
        _api.MapJson(Wiki + "/rest/api/user/current", new { accountId = "svc" });
        _api.MapJson(Wiki + "/api/v2/spaces", new { results = new[] { new { id = "100", key = "ENG", name = "Engineering", type = "global" } } });
        _api.MapJson(Wiki + "/api/v2/pages", new { results = new[] { new { id = "7" } } });
        _api.MapJson(Wiki + "/rest/api/search/user", new
        {
            results = new object[] { new { user = new { accountId = "svc" } }, new { user = new { accountId = "other" } } },
        });
        _api.MapJson(Wiki + "/rest/api/content/7/permission/check", new { hasPermission = true });

        _jira = new FakeJira(_api, CloudId).MapSiteTest();
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

    private async Task<Guid> CreateSiteAsync(string host)
    {
        var response = await Admin.PostAsJsonAsync("/api/v1/atlassian/sites",
            new { siteUrl = $"https://{host}", clientId = "client-1", clientSecret = Secret });
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
        body.GetProperty("name").GetString().Should().Be("jira-api-create.atlassian.net / Engineering");

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
    public async Task Create_SiteWithoutJira_400()
    {
        // Jira refuses the service account, so the site is saved for Confluence alone.
        _jira.Answer("/myself", HttpStatusCode.Forbidden);
        Guid site = await CreateSiteAsync("jira-api-nojira.atlassian.net");

        var response = await CreateSourceAsync(site, new { kind = "jira-project", projectId = "10000" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).GetProperty("error").GetString()
            .Should().Be("This site's last test didn't pass for Jira. Test it on the Connections page.");
        (await SourceCountAsync(site)).Should().Be(0);

        // A scope without a kind is still Confluence's, as before Jira.
        (await CreateSourceAsync(site, new { spaceId = "100" })).StatusCode.Should().Be(HttpStatusCode.Created);
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
