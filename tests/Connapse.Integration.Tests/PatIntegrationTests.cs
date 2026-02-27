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
/// Integration tests for Personal Access Token CRUD and authentication.
/// Verifies that tokens can be created, listed, revoked, and that revoked
/// tokens are rejected by the API key authentication handler.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class PatIntegrationTests : IAsyncLifetime
{
    private const string AdminEmail = "admin@pat-tests.connapse.io";
    private const string AdminPassword = "AdminTest1!";
    private const string OtherEmail = "other@pat-tests.connapse.io";
    private const string OtherPassword = "OtherTest1!";
    private const string TestJwtSecret =
        "test-jwt-secret-for-pat-integration-tests-must-be-64-chars-ok!!";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("connapse_pat_test")
        .WithUsername("pat_test")
        .WithPassword("pat_test")
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

        await SeedUserAsync(OtherEmail, OtherPassword, "Viewer");
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

    private async Task<string> GetJwtAsync(string email, string password)
    {
        var response = await _anonClient.PostAsJsonAsync(
            "/api/v1/auth/token", new { email, password });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions);
        return token!.AccessToken;
    }

    private HttpClient CreateJwtClient(string accessToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private HttpClient CreatePatClient(string apiKey)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    // ── Create ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreatePat_Unauthenticated_Returns401()
    {
        var response = await _anonClient.PostAsJsonAsync(
            "/api/v1/auth/pats", new { name = "Test" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreatePat_AuthenticatedUser_Returns200WithTokenAndPrefix()
    {
        var jwt = await GetJwtAsync(AdminEmail, AdminPassword);
        using var client = CreateJwtClient(jwt);

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/pats", new { name = "My CLI Token" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PatCreateResponse>(JsonOptions);
        body.Should().NotBeNull();
        body!.Token.Should().StartWith("cnp_");
        body.Name.Should().Be("My CLI Token");
    }

    // ── List ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListPats_AuthenticatedUser_ReturnsOnlyOwnTokens()
    {
        // Create PATs for two different users
        var adminJwt = await GetJwtAsync(AdminEmail, AdminPassword);
        using var adminClient = CreateJwtClient(adminJwt);
        await adminClient.PostAsJsonAsync("/api/v1/auth/pats", new { name = "Admin Token" });

        var otherJwt = await GetJwtAsync(OtherEmail, OtherPassword);
        using var otherClient = CreateJwtClient(otherJwt);
        await otherClient.PostAsJsonAsync("/api/v1/auth/pats", new { name = "Other Token" });

        // Admin lists own PATs
        var listResponse = await adminClient.GetAsync("/api/v1/auth/pats");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var pats = await listResponse.Content.ReadFromJsonAsync<PatListItem[]>(JsonOptions);
        pats.Should().NotBeNull();
        pats!.Should().AllSatisfy(p => p.Name.Should().NotBe("Other Token"));
    }

    // ── Revoke ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RevokePat_ValidId_Returns204()
    {
        var jwt = await GetJwtAsync(AdminEmail, AdminPassword);
        using var client = CreateJwtClient(jwt);

        var createResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/pats", new { name = "Revoke Me" });
        var created = await createResponse.Content.ReadFromJsonAsync<PatCreateResponse>(JsonOptions);

        var revokeResponse = await client.DeleteAsync($"/api/v1/auth/pats/{created!.Id}");

        revokeResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task RevokePat_OtherUsersToken_Returns404()
    {
        var adminJwt = await GetJwtAsync(AdminEmail, AdminPassword);
        using var adminClient = CreateJwtClient(adminJwt);
        var createResponse = await adminClient.PostAsJsonAsync(
            "/api/v1/auth/pats", new { name = "Admin Token" });
        var created = await createResponse.Content.ReadFromJsonAsync<PatCreateResponse>(JsonOptions);

        // Other user tries to revoke admin's PAT
        var otherJwt = await GetJwtAsync(OtherEmail, OtherPassword);
        using var otherClient = CreateJwtClient(otherJwt);

        var revokeResponse = await otherClient.DeleteAsync($"/api/v1/auth/pats/{created!.Id}");

        revokeResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PAT auth ──────────────────────────────────────────────────────────

    [Fact]
    public async Task UseRevokedPat_Returns401()
    {
        // Create a PAT then immediately revoke it
        var jwt = await GetJwtAsync(AdminEmail, AdminPassword);
        using var jwtClient = CreateJwtClient(jwt);

        var createResponse = await jwtClient.PostAsJsonAsync(
            "/api/v1/auth/pats", new { name = "Short-Lived" });
        var created = await createResponse.Content.ReadFromJsonAsync<PatCreateResponse>(JsonOptions);
        await jwtClient.DeleteAsync($"/api/v1/auth/pats/{created!.Id}");

        // Attempt to use the now-revoked token
        using var patClient = CreatePatClient(created.Token);
        var response = await patClient.GetAsync("/api/containers");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UseValidPat_Returns200()
    {
        var jwt = await GetJwtAsync(AdminEmail, AdminPassword);
        using var jwtClient = CreateJwtClient(jwt);

        var createResponse = await jwtClient.PostAsJsonAsync(
            "/api/v1/auth/pats", new { name = "Valid PAT" });
        var created = await createResponse.Content.ReadFromJsonAsync<PatCreateResponse>(JsonOptions);

        using var patClient = CreatePatClient(created!.Token);
        var response = await patClient.GetAsync("/api/containers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
