using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using Connapse.Identity.Data.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;

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
    private const string AdminEmail = "admin@authz-tests.connapse.io";
    private const string AdminPassword = "AdminTest1!";
    private const string EditorEmail = "editor@authz-tests.connapse.io";
    private const string EditorPassword = "EditorTest1!";
    private const string ViewerEmail = "viewer@authz-tests.connapse.io";
    private const string ViewerPassword = "ViewerTest1!";
    private const string TestJwtSecret =
        "test-jwt-secret-for-authz-integration-tests-must-be-64-chars!!";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("connapse_authz_test")
        .WithUsername("authz_test")
        .WithPassword("authz_test")
        .Build();

    private readonly MinioContainer _minio = new MinioBuilder()
        .WithImage("minio/minio")
        .Build();

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _anonClient = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await _minio.StartAsync();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:DefaultConnection", _postgres.GetConnectionString());
                builder.UseSetting("Knowledge:Storage:MinIO:Endpoint",
                    $"{_minio.Hostname}:{_minio.GetMappedPublicPort(9000)}");
                builder.UseSetting("Knowledge:Storage:MinIO:AccessKey", MinioBuilder.DefaultUsername);
                builder.UseSetting("Knowledge:Storage:MinIO:SecretKey", MinioBuilder.DefaultPassword);
                builder.UseSetting("Knowledge:Storage:MinIO:UseSSL", "false");
                builder.UseSetting("CONNAPSE_ADMIN_EMAIL", AdminEmail);
                builder.UseSetting("CONNAPSE_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("Identity:Jwt:Secret", TestJwtSecret);
            });

        _anonClient = _factory.CreateClient();
        await Task.Delay(2000);

        await SeedUserAsync(EditorEmail, EditorPassword, "Editor");
        await SeedUserAsync(ViewerEmail, ViewerPassword, "Viewer");
    }

    public async Task DisposeAsync()
    {
        _anonClient.Dispose();
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
        await _minio.DisposeAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task SeedUserAsync(string email, string password, string role)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();

        if (await userManager.FindByEmailAsync(email) is not null)
            return;

        var user = new ConnapseUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = $"Test {role}",
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
        var client = _factory.CreateClient();
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
        var token = await GetTokenAsync(AdminEmail, AdminPassword);
        using var client = CreateClientWithToken(token);

        var response = await client.GetAsync("/api/settings/embedding");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetUsers_Admin_Returns200()
    {
        var token = await GetTokenAsync(AdminEmail, AdminPassword);
        using var client = CreateClientWithToken(token);

        var response = await client.GetAsync("/api/v1/auth/users");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
