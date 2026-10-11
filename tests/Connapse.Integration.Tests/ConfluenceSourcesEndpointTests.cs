using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Connectors;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// <c>POST</c> and <c>PATCH /api/sources</c> on an Atlassian site run the checks the New source
/// dialog runs: the space must be one the service account can view now, not already a source, and
/// the attachment cap in range. Field names in errors are the scope keys the dialog writes.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class ConfluenceSourcesEndpointTests(SharedWebAppFixture fixture) : IAsyncLifetime
{
    private const string CloudId = "11111111-2222-3333-4444-555555555555";
    private const string Root = "/ex/confluence/" + CloudId + "/wiki";
    private const string Secret = "confluence-sources-secret-123";

    private readonly FakeAtlassianApi _api = fixture.Atlassian;

    public Task InitializeAsync()
    {
        // The fake is shared across the collection: put it in a known state.
        _api.FailTokenWith(null);
        _api.AnswerTokenWith(null);
        _api.FailWith(null);
        _api.MapJson(FakeAtlassianApi.TenantInfoPath, new { cloudId = CloudId });
        _api.MapJson(Root + "/rest/api/user/current", new { accountId = "svc" });
        _api.MapJson(Root + "/api/v2/spaces", new
        {
            results = new object[]
            {
                new { id = "100", key = "ENG", name = "Engineering", type = "global" },
                new { id = "200", key = "HB", name = "Handbook", type = "global" },
                new { id = "300", key = "~pat", name = "Pat's notes", type = "personal" },
            },
        });
        _api.MapJson(Root + "/api/v2/pages", new { results = new[] { new { id = "7" } } });
        _api.MapJson(Root + "/rest/api/search/user", new
        {
            results = new object[] { new { user = new { accountId = "svc" } }, new { user = new { accountId = "other" } } },
        });
        _api.MapJson(Root + "/rest/api/content/7/permission/check", new { hasPermission = true });
        return Task.CompletedTask;
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

    /// <summary>A site whose test confirmed every scope, the attachment one included.</summary>
    private async Task<Guid> CreateSiteWithAttachmentScopeAsync(string host)
    {
        _api.TokenScope = string.Join(' ', Connapse.Storage.ConnectionTesters.AtlassianSiteScopes.All.Select(s => s.Name));
        try
        {
            return await CreateSiteAsync(host);
        }
        finally
        {
            _api.TokenScope = null;
        }
    }

    [Fact]
    public async Task Create_SiteWithoutAConfirmedAttachmentScope_DefaultsAttachmentsOffButHonoursAnExplicitOn()
    {
        Guid site = await CreateSiteAsync("confluence-api-noattach.atlassian.net");

        var defaulted = await CreateSourceAsync(site, new { spaceId = "100" });
        var explicitOn = await CreateSourceAsync(site, new { spaceId = "200", includeAttachments = true });

        defaulted.StatusCode.Should().Be(HttpStatusCode.Created, await defaulted.Content.ReadAsStringAsync());
        using var off = JsonDocument.Parse(await StoredScopeAsync((await BodyAsync(defaulted)).GetProperty("id").GetGuid()));
        off.RootElement.GetProperty("includeAttachments").GetBoolean().Should().BeFalse();
        using var on = JsonDocument.Parse(await StoredScopeAsync((await BodyAsync(explicitOn)).GetProperty("id").GetGuid()));
        on.RootElement.GetProperty("includeAttachments").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Create_SpaceOnTheSite_IsNamedLikeTheDialogAndTakesItsKeyFromAtlassian()
    {
        Guid site = await CreateSiteWithAttachmentScopeAsync("confluence-api-create.atlassian.net");

        var response = await CreateSourceAsync(site, new { spaceId = "100", spaceKey = "WRONG", maxAttachmentMb = 40 });

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await BodyAsync(response);
        body.GetProperty("name").GetString().Should().Be("confluence-api-create.atlassian.net / Engineering");

        using var scope = JsonDocument.Parse(await StoredScopeAsync(body.GetProperty("id").GetGuid()));
        scope.RootElement.GetProperty("kind").GetString().Should().Be("confluence-space");
        scope.RootElement.GetProperty("spaceId").GetString().Should().Be("100");
        scope.RootElement.GetProperty("spaceKey").GetString().Should().Be("ENG");
        scope.RootElement.GetProperty("includeAttachments").GetBoolean().Should().BeTrue();
        scope.RootElement.GetProperty("maxAttachmentMb").GetInt32().Should().Be(40);
    }

    [Fact]
    public async Task Create_WithAName_KeepsTheName()
    {
        Guid site = await CreateSiteAsync("confluence-api-named.atlassian.net");

        var response = await CreateSourceAsync(site, new { spaceId = 200 }, name: "confluence-api-handbook");

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        (await BodyAsync(response)).GetProperty("name").GetString().Should().Be("confluence-api-handbook");
    }

    [Fact]
    public async Task Create_SpaceTheServiceAccountCannotSee_Returns400NamingSpaceIdAndCreatesNothing()
    {
        Guid site = await CreateSiteAsync("confluence-api-unseen.atlassian.net");

        var response = await CreateSourceAsync(site, new { spaceId = "999" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).GetProperty("field").GetString().Should().Be("spaceId");
        (await SourceCountAsync(site)).Should().Be(0);
    }

    [Fact]
    public async Task Create_SameSpaceTwice_Returns409()
    {
        Guid site = await CreateSiteAsync("confluence-api-twice.atlassian.net");
        (await CreateSourceAsync(site, new { spaceId = "100" })).StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await CreateSourceAsync(site, new { spaceId = "100" }, name: "a-different-name");

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await BodyAsync(second)).GetProperty("field").GetString().Should().Be("spaceId");
        (await SourceCountAsync(site)).Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Create_MaxAttachmentSizeOutOfRange_Returns400NamingTheField(int mb)
    {
        // Attachments on (by default here), since the size is checked only when they are.
        Guid site = await CreateSiteWithAttachmentScopeAsync($"confluence-api-size-{mb}.atlassian.net");

        var response = await CreateSourceAsync(site, new { spaceId = "100", maxAttachmentMb = mb });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await BodyAsync(response);
        body.GetProperty("field").GetString().Should().Be("maxAttachmentMb");
        body.GetProperty("error").GetString().Should().Contain("between 1 and 100 MB");
        (await SourceCountAsync(site)).Should().Be(0);
    }

    [Fact]
    public async Task Create_NoSpaceId_Returns400NamingSpaceId()
    {
        Guid site = await CreateSiteAsync("confluence-api-nospace.atlassian.net");

        var response = await CreateSourceAsync(site, new { includeAttachments = false });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).GetProperty("field").GetString().Should().Be("spaceId");
    }

    [Fact]
    public async Task Create_AtlassianUnreachable_Returns502WithoutTheSecretAndCreatesNothing()
    {
        Guid site = await CreateSiteAsync("confluence-api-down.atlassian.net");
        _api.FailWith(HttpStatusCode.InternalServerError);

        var response = await CreateSourceAsync(site, new { spaceId = "100" });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(Secret);
        (await SourceCountAsync(site)).Should().Be(0);
    }

    [Fact]
    public async Task Patch_AttachmentSettings_AreCheckedAndTheSpaceIsKept()
    {
        Guid site = await CreateSiteWithAttachmentScopeAsync("confluence-api-patch.atlassian.net");
        var created = await CreateSourceAsync(site, new { spaceId = "100" });
        Guid sourceId = (await BodyAsync(created)).GetProperty("id").GetGuid();

        var outOfRange = await Admin.PatchAsJsonAsync($"/api/sources/{sourceId}",
            new { scopeJson = JsonSerializer.Serialize(new { maxAttachmentMb = 500 }) });
        outOfRange.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(outOfRange)).GetProperty("field").GetString().Should().Be("maxAttachmentMb");

        var moved = await Admin.PatchAsJsonAsync($"/api/sources/{sourceId}",
            new { scopeJson = JsonSerializer.Serialize(new { spaceId = "200" }) });
        moved.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(moved)).GetProperty("field").GetString().Should().Be("spaceId");

        var ok = await Admin.PatchAsJsonAsync($"/api/sources/{sourceId}",
            new { scopeJson = JsonSerializer.Serialize(new { includeAttachments = false, maxAttachmentMb = 10 }) });
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());

        using var scope = JsonDocument.Parse(await StoredScopeAsync(sourceId));
        scope.RootElement.GetProperty("spaceId").GetString().Should().Be("100");
        scope.RootElement.GetProperty("spaceKey").GetString().Should().Be("ENG");
        scope.RootElement.GetProperty("kind").GetString().Should().Be("confluence-space");
        scope.RootElement.GetProperty("includeAttachments").GetBoolean().Should().BeFalse();
        scope.RootElement.GetProperty("maxAttachmentMb").GetInt32().Should().Be(10);
    }
}
