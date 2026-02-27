using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using Connapse.Identity.Data.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for RBAC enforcement on the API layer.
/// Verifies that 401 (no auth) and 403 (wrong role) are returned correctly,
/// and that each role gets access to the correct operations.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class AuthorizationIntegrationTests : IAsyncLifetime
{
    private const string EditorEmail = "editor@authz-tests.connapse.io";
    private const string EditorPassword = "EditorTest1!";
    private const string ViewerEmail = "viewer@authz-tests.connapse.io";
    private const string ViewerPassword = "ViewerTest1!";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SharedWebAppFixture _fixture;
    private HttpClient _anonClient = null!;

    public AuthorizationIntegrationTests(SharedWebAppFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _anonClient = _fixture.Factory.CreateClient();
        await SeedUserAsync(EditorEmail, EditorPassword, "Editor");
        await SeedUserAsync(ViewerEmail, ViewerPassword, "Viewer");
    }

    public Task DisposeAsync()
    {
        _anonClient.Dispose();
        return Task.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task SeedUserAsync(string email, string password, string role)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();

        if (await userManager.FindByEmailAsync(email) is not null)
            return;

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

    private async Task<string> GetTokenAsync(string email, string password)
    {
        var response = await _anonClient.PostAsJsonAsync(
            "/api/v1/auth/token", new { email, password });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions);
        return token!.AccessToken;
    }

    private HttpClient CreateClientWithToken(string accessToken)
    {
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    // ── Unauthenticated → 401 ─────────────────────────────────────────────

    [Fact]
    public async Task GetContainers_NoAuth_Returns401()
    {
        var response = await _anonClient.GetAsync("/api/containers");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostContainer_NoAuth_Returns401()
    {
        var response = await _anonClient.PostAsJsonAsync(
            "/api/containers", new { name = "test" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetSettings_NoAuth_Returns401()
    {
        var response = await _anonClient.GetAsync("/api/settings/embedding");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetUsers_NoAuth_Returns401()
    {
        var response = await _anonClient.GetAsync("/api/v1/auth/users");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Viewer: read OK, write → 403 ──────────────────────────────────────

    [Fact]
    public async Task GetContainers_Viewer_Returns200()
    {
        var token = await GetTokenAsync(ViewerEmail, ViewerPassword);
        using var client = CreateClientWithToken(token);

        var response = await client.GetAsync("/api/containers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostContainer_Viewer_Returns403()
    {
        var token = await GetTokenAsync(ViewerEmail, ViewerPassword);
        using var client = CreateClientWithToken(token);

        var response = await client.PostAsJsonAsync(
            "/api/containers", new { name = "test-viewer-container" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetSettings_Viewer_Returns403()
    {
        var token = await GetTokenAsync(ViewerEmail, ViewerPassword);
        using var client = CreateClientWithToken(token);

        var response = await client.GetAsync("/api/settings/embedding");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetUsers_Viewer_Returns403()
    {
        var token = await GetTokenAsync(ViewerEmail, ViewerPassword);
        using var client = CreateClientWithToken(token);

        var response = await client.GetAsync("/api/v1/auth/users");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Editor: write OK ──────────────────────────────────────────────────

    [Fact]
    public async Task PostContainer_Editor_Returns201()
    {
        var token = await GetTokenAsync(EditorEmail, EditorPassword);
        using var client = CreateClientWithToken(token);

        var response = await client.PostAsJsonAsync(
            "/api/containers", new { name = "editor-test-container" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ── Admin: all access ─────────────────────────────────────────────────

    [Fact]
    public async Task GetSettings_Admin_Returns200()
    {
        var token = await GetTokenAsync(SharedWebAppFixture.AdminEmail, SharedWebAppFixture.AdminPassword);
        using var client = CreateClientWithToken(token);

        var response = await client.GetAsync("/api/settings/embedding");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetUsers_Admin_Returns200()
    {
        var token = await GetTokenAsync(SharedWebAppFixture.AdminEmail, SharedWebAppFixture.AdminPassword);
        using var client = CreateClientWithToken(token);

        var response = await client.GetAsync("/api/v1/auth/users");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
