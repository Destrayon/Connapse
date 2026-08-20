using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Data.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// The <c>/api/sources</c> surface.
/// <para>
/// Two properties matter more than the CRUD: responses must never carry the source's scope,
/// and mutations must require an administrator rather than an editor. Both are asserted
/// negatively — the absence of a field, the refusal of a role — because a later refactor that
/// serializes the record directly, or copies the container endpoints' authorization, would
/// otherwise pass every positive test.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SourcesEndpointsTests(SharedWebAppFixture fixture) : IAsyncLifetime
{
    private const string EditorEmail = "editor@sources-tests.connapse.io";
    private const string EditorPassword = "EditorSrcTest1!";
    private const string ViewerEmail = "viewer@sources-tests.connapse.io";
    private const string ViewerPassword = "ViewerSrcTest1!";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private HttpClient _editorClient = null!;
    private HttpClient _viewerClient = null!;

    private HttpClient Admin => fixture.AdminClient;

    public async Task InitializeAsync()
    {
        await SeedUserAsync(EditorEmail, EditorPassword, "Editor");
        await SeedUserAsync(ViewerEmail, ViewerPassword, "Viewer");
        _editorClient = await ClientForAsync(EditorEmail, EditorPassword);
        _viewerClient = await ClientForAsync(ViewerEmail, ViewerPassword);
    }

    public Task DisposeAsync()
    {
        _editorClient.Dispose();
        _viewerClient.Dispose();
        return Task.CompletedTask;
    }

    private async Task SeedUserAsync(string email, string password, string role)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();

        if (await userManager.FindByEmailAsync(email) is not null) return;

        var user = new ConnapseUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = email,
            CreatedAt = DateTime.UtcNow,
        };

        var result = await userManager.CreateAsync(user, password);
        result.Succeeded.Should().BeTrue(
            because: string.Join(", ", result.Errors.Select(e => e.Description)));
        await userManager.AddToRoleAsync(user, role);
    }

    private async Task<HttpClient> ClientForAsync(string email, string password)
    {
        using var anon = fixture.Factory.CreateClient();
        var response = await anon.PostAsJsonAsync("/api/v1/auth/token", new { email, password });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var token = await response.Content.ReadFromJsonAsync<TokenPayload>(JsonOptions);

        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    private sealed record TokenPayload(string AccessToken);

    private static string ShortName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    /// <summary>Creates a connection directly, since there is no REST route to create one.</summary>
    private async Task<Guid> SeedConnectionAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var connection = await connections.CreateAsync(
            new CreateConnectionRequest(ShortName("conn"), ConnectionProvider.S3, """{"region":"us-east-1"}"""),
            createdByUserId: null);
        return connection.Id;
    }

    private async Task<Guid> CreateSourceAsync(Guid connectionId, string? name = null)
    {
        var response = await Admin.PostAsJsonAsync("/api/sources", new
        {
            name = name ?? ShortName("src"),
            connectionId,
            scopeJson = """{"bucketName":"b","prefix":"team/"}""",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty("id").GetGuid();
    }

    // ── The scope must never come back ────────────────────────────────────

    [Fact]
    public async Task GetSource_ResponseOmitsScopeAndCursor()
    {
        Guid connectionId = await SeedConnectionAsync();
        Guid sourceId = await CreateSourceAsync(connectionId);

        var response = await Admin.GetAsync($"/api/sources/{sourceId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();

        // Positive first, so the negatives below cannot pass on an empty or error body.
        body.Should().Contain("\"kind\":\"source\"");
        body.Should().Contain("lastSyncStatus");

        // Asserted on the raw body rather than a deserialized DTO: deserializing into
        // SourceResponse would drop unexpected fields silently, which is exactly the
        // regression this guards against.
        body.Should().NotContain("scopeJson", "the scope names buckets and paths");
        body.Should().NotContain("bucketName", "the scope's contents must not leak either");
        body.Should().NotContain("syncCursor", "a provider continuation token is not the caller's business");
    }

    [Fact]
    public async Task ListSources_ResponseOmitsScope()
    {
        Guid connectionId = await SeedConnectionAsync();
        await CreateSourceAsync(connectionId);

        var response = await Admin.GetAsync("/api/sources?take=50");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("\"kind\":\"source\"", "the listing must actually contain a source");
        body.Should().NotContain("scopeJson");
        body.Should().NotContain("bucketName");
    }

    [Fact]
    public async Task GetSource_AsViewer_OmitsTheSyncErrorDetail()
    {
        Guid connectionId = await SeedConnectionAsync();
        Guid sourceId = await CreateSourceAsync(connectionId);

        // A provider's failure text routinely quotes the resource that failed, so handing it
        // to every reader would give back the infrastructure detail scopeJson is withheld to
        // protect.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
            await sources.UpdateSyncStateAsync(
                sourceId, null, SyncStatus.Failed,
                "Access Denied for bucket payroll-secrets", DateTime.UtcNow);
        }

        var asViewer = await _viewerClient.GetAsync($"/api/sources/{sourceId}");
        asViewer.StatusCode.Should().Be(HttpStatusCode.OK);
        (await asViewer.Content.ReadAsStringAsync())
            .Should().NotContain("payroll-secrets");

        var asAdmin = await Admin.GetAsync($"/api/sources/{sourceId}");
        (await asAdmin.Content.ReadAsStringAsync())
            .Should().Contain("payroll-secrets", "an administrator needs the failure reason");
    }

    // ── Mutations are administrator-only ──────────────────────────────────

    [Fact]
    public async Task CreateSource_AsEditor_IsForbidden()
    {
        Guid connectionId = await SeedConnectionAsync();

        var response = await _editorClient.PostAsJsonAsync("/api/sources", new
        {
            name = ShortName("src"),
            connectionId,
            scopeJson = "{}",
        });

        // Editor is enough to manage container content, but choosing what external data gets
        // indexed is an administrative act.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DeleteSource_AsEditor_IsForbidden()
    {
        Guid connectionId = await SeedConnectionAsync();
        Guid sourceId = await CreateSourceAsync(connectionId);

        var response = await _editorClient.DeleteAsync($"/api/sources/{sourceId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ListSources_AsViewer_IsAllowed()
    {
        var response = await _viewerClient.GetAsync("/api/sources");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── CRUD ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSource_WithUnknownConnection_IsRejected()
    {
        // A source whose connection does not exist is skipped silently by the sync service,
        // so it would look created and never sync.
        var response = await Admin.PostAsJsonAsync("/api/sources", new
        {
            name = ShortName("src"),
            connectionId = Guid.NewGuid(),
            scopeJson = "{}",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateSource_WithMalformedScopeJson_IsRejected()
    {
        Guid connectionId = await SeedConnectionAsync();

        var response = await Admin.PostAsJsonAsync("/api/sources", new
        {
            name = ShortName("src"),
            connectionId,
            scopeJson = "{not valid json",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateSource_DuplicateName_IsConflict()
    {
        Guid connectionId = await SeedConnectionAsync();
        string name = ShortName("dup");
        await CreateSourceAsync(connectionId, name);

        var response = await Admin.PostAsJsonAsync("/api/sources", new
        {
            name,
            connectionId,
            scopeJson = "{}",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task UpdateSource_DisablesIt()
    {
        Guid connectionId = await SeedConnectionAsync();
        Guid sourceId = await CreateSourceAsync(connectionId);

        var response = await Admin.PatchAsJsonAsync($"/api/sources/{sourceId}", new { enabled = false });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await response.Content.ReadFromJsonAsync<JsonElement>();
        updated.GetProperty("enabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task DeleteSource_ThenGet_IsNotFound()
    {
        Guid connectionId = await SeedConnectionAsync();
        Guid sourceId = await CreateSourceAsync(connectionId);

        (await Admin.DeleteAsync($"/api/sources/{sourceId}")).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        (await Admin.GetAsync($"/api/sources/{sourceId}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetSource_UnknownId_IsNotFound()
    {
        (await Admin.GetAsync($"/api/sources/{Guid.NewGuid()}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SyncSource_WhenDisabled_IsRejected()
    {
        Guid connectionId = await SeedConnectionAsync();
        Guid sourceId = await CreateSourceAsync(connectionId);
        await Admin.PatchAsJsonAsync($"/api/sources/{sourceId}", new { enabled = false });

        var response = await Admin.PostAsync($"/api/sources/{sourceId}/sync", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── No enumeration surface ────────────────────────────────────────────

    [Fact]
    public async Task NoBrowseOrDocumentRouteExistsForASource()
    {
        Guid connectionId = await SeedConnectionAsync();
        Guid sourceId = await CreateSourceAsync(connectionId);

        // The whole point of the source/container split: a source is a searchable scope, not
        // a browsable folder tree. These must not quietly start working.
        foreach (string path in new[]
                 {
                     $"/api/sources/{sourceId}/documents",
                     $"/api/sources/{sourceId}/browse",
                     $"/api/sources/{sourceId}/files",
                 })
        {
            var response = await Admin.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{path} must not exist");
        }
    }

    [Fact]
    public async Task SourceId_OnTheContainerDocumentRoute_IsRejected()
    {
        Guid connectionId = await SeedConnectionAsync();
        Guid sourceId = await CreateSourceAsync(connectionId);

        // Container routes validate that the id is a container before touching documents, so
        // a source id must 404 rather than enumerate the source through the other surface.
        var response = await Admin.GetAsync($"/api/containers/{sourceId}/documents");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
    }
}
