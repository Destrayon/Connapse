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
/// Integration tests for auth endpoints: JWT token issuance/refresh,
/// PAT management, and user/role administration.
/// Routes under: /api/v1/auth/
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class AuthEndpointTests : IAsyncLifetime
{
    private const string AdminEmail = "admin@auth-test.connapse.io";
    private const string AdminPassword = "AdminTest1!";
    private const string ViewerEmail = "viewer@auth-test.connapse.io";
    private const string ViewerPassword = "ViewerTest1!";

    // 64-char secret satisfies the HS256 key-size requirement and is deterministic across runs
    private const string TestJwtSecret =
        "test-jwt-secret-for-auth-integration-tests-must-be-64-chars-ok!";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("connapse_auth_test")
        .WithUsername("auth_test")
        .WithPassword("auth_test")
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
                // Seed the admin user on startup via env vars
                builder.UseSetting("CONNAPSE_ADMIN_EMAIL", AdminEmail);
                builder.UseSetting("CONNAPSE_ADMIN_PASSWORD", AdminPassword);
                // Pin a deterministic JWT secret so tokens can be validated
                builder.UseSetting("Identity:Jwt:Secret", TestJwtSecret);
            });

        _anonClient = _factory.CreateClient();

        // Give migrations + AdminSeedService time to finish
        await Task.Delay(2000);

        // Create a Viewer user for tests that need a non-admin authenticated identity
        await SeedViewerUserAsync();
    }

    public async Task DisposeAsync()
    {
        _anonClient.Dispose();
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
        await _minio.DisposeAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private async Task SeedViewerUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();

        if (await userManager.FindByEmailAsync(ViewerEmail) is not null)
            return;

        var viewer = new ConnapseUser
        {
            UserName = ViewerEmail,
            Email = ViewerEmail,
            EmailConfirmed = true,
            DisplayName = "Test Viewer",
            CreatedAt = DateTime.UtcNow,
        };

        var createResult = await userManager.CreateAsync(viewer, ViewerPassword);
        createResult.Succeeded.Should().BeTrue(
            because: string.Join(", ", createResult.Errors.Select(e => e.Description)));

        await userManager.AddToRoleAsync(viewer, "Viewer");
    }

    /// <summary>Gets a fresh JWT token pair for the given credentials.</summary>
    private async Task<TokenResponse> GetTokenAsync(string email, string password)
    {
        var response = await _anonClient.PostAsJsonAsync(
            "/api/v1/auth/token", new LoginRequest(email, password));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: $"login as {email} should succeed");

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions);
        token.Should().NotBeNull();
        return token!;
    }

    /// <summary>Creates an HttpClient pre-configured with a Bearer token.</summary>
    private HttpClient CreateAuthenticatedClient(string accessToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    // ── POST /api/v1/auth/token ──────────────────────────────────────────

    [Fact]
    public async Task GetToken_ValidAdminCredentials_Returns200WithTokenPair()
    {
        var response = await _anonClient.PostAsJsonAsync(
            "/api/v1/auth/token", new LoginRequest(AdminEmail, AdminPassword));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions);
        body.Should().NotBeNull();
        body!.AccessToken.Should().NotBeNullOrEmpty();
        body.RefreshToken.Should().NotBeNullOrEmpty();
        body.TokenType.Should().Be("Bearer");
        body.ExpiresAt.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task GetToken_WrongPassword_Returns401()
    {
        var response = await _anonClient.PostAsJsonAsync(
            "/api/v1/auth/token", new LoginRequest(AdminEmail, "WrongPassword1!"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetToken_UnknownEmail_Returns401()
    {
        var response = await _anonClient.PostAsJsonAsync(
            "/api/v1/auth/token", new LoginRequest("nobody@nowhere.invalid", "SomePassword1!"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetToken_ValidCredentials_UpdatesLastLoginAt()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);

        await _anonClient.PostAsJsonAsync(
            "/api/v1/auth/token", new LoginRequest(AdminEmail, AdminPassword));

        // Verify LastLoginAt was set by reading the user from the DB
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();
        var user = await userManager.FindByEmailAsync(AdminEmail);
        user!.LastLoginAt.Should().NotBeNull()
            .And.BeAfter(before);
    }

    // ── POST /api/v1/auth/token/refresh ─────────────────────────────────

    [Fact]
    public async Task RefreshToken_ValidToken_Returns200WithNewTokenPair()
    {
        var initial = await GetTokenAsync(AdminEmail, AdminPassword);

        var response = await _anonClient.PostAsJsonAsync(
            "/api/v1/auth/token/refresh", new RefreshTokenRequest(initial.RefreshToken));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions);
        body.Should().NotBeNull();
        body!.AccessToken.Should().NotBeNullOrEmpty();
        body.RefreshToken.Should().NotBeNullOrEmpty();
        body.ExpiresAt.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task RefreshToken_InvalidToken_Returns401()
    {
        var response = await _anonClient.PostAsJsonAsync(
            "/api/v1/auth/token/refresh", new RefreshTokenRequest("not-a-real-refresh-token"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RefreshToken_UsedTokenCannotBeReused_Returns401()
    {
        var initial = await GetTokenAsync(AdminEmail, AdminPassword);

        // First refresh: should succeed
        var firstRefresh = await _anonClient.PostAsJsonAsync(
            "/api/v1/auth/token/refresh", new RefreshTokenRequest(initial.RefreshToken));
        firstRefresh.StatusCode.Should().Be(HttpStatusCode.OK);

        // Second refresh with the same (now revoked) token: should fail
        var secondRefresh = await _anonClient.PostAsJsonAsync(
            "/api/v1/auth/token/refresh", new RefreshTokenRequest(initial.RefreshToken));
        secondRefresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── GET /api/v1/auth/pats ────────────────────────────────────────────

    [Fact]
    public async Task ListPats_Unauthenticated_Returns401()
    {
        var response = await _anonClient.GetAsync("/api/v1/auth/pats");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListPats_Authenticated_Returns200WithList()
    {
        var token = await GetTokenAsync(AdminEmail, AdminPassword);
        using var client = CreateAuthenticatedClient(token.AccessToken);

        var response = await client.GetAsync("/api/v1/auth/pats");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var pats = await response.Content.ReadFromJsonAsync<List<PatListItem>>(JsonOptions);
        pats.Should().NotBeNull();
    }

    // ── POST /api/v1/auth/pats ───────────────────────────────────────────

    [Fact]
    public async Task CreatePat_Unauthenticated_Returns401()
    {
        var response = await _anonClient.PostAsJsonAsync(
            "/api/v1/auth/pats", new PatCreateRequest("Test PAT"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreatePat_Authenticated_Returns200WithRawToken()
    {
        var token = await GetTokenAsync(AdminEmail, AdminPassword);
        using var client = CreateAuthenticatedClient(token.AccessToken);

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/pats", new PatCreateRequest("My Test PAT", ["knowledge:read"]));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<PatCreateResponse>(JsonOptions);
        body.Should().NotBeNull();
        body!.Token.Should().StartWith("cnp_");
        body.Name.Should().Be("My Test PAT");
        body.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task CreatePat_NewPat_AppearsInSubsequentList()
    {
        var token = await GetTokenAsync(AdminEmail, AdminPassword);
        using var client = CreateAuthenticatedClient(token.AccessToken);

        var createResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/pats", new PatCreateRequest("Listable PAT"));
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var created = await createResponse.Content.ReadFromJsonAsync<PatCreateResponse>(JsonOptions);

        var listResponse = await client.GetAsync("/api/v1/auth/pats");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var pats = await listResponse.Content.ReadFromJsonAsync<List<PatListItem>>(JsonOptions);

        pats.Should().Contain(p => p.Id == created!.Id && p.Name == "Listable PAT");
    }

    [Fact]
    public async Task CreatePat_PatCanBeUsedForAuthentication()
    {
        var token = await GetTokenAsync(AdminEmail, AdminPassword);
        using var jwtClient = CreateAuthenticatedClient(token.AccessToken);

        var createResponse = await jwtClient.PostAsJsonAsync(
            "/api/v1/auth/pats", new PatCreateRequest("Auth Test PAT"));
        var created = await createResponse.Content.ReadFromJsonAsync<PatCreateResponse>(JsonOptions);

        // Use the PAT via X-Api-Key header to call a protected endpoint
        using var patClient = _factory.CreateClient();
        patClient.DefaultRequestHeaders.Add("X-Api-Key", created!.Token);

        var listResponse = await patClient.GetAsync("/api/v1/auth/pats");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── DELETE /api/v1/auth/pats/{id} ────────────────────────────────────

    [Fact]
    public async Task RevokePat_Unauthenticated_Returns401()
    {
        var response = await _anonClient.DeleteAsync($"/api/v1/auth/pats/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RevokePat_NonExistentPat_Returns404()
    {
        var token = await GetTokenAsync(AdminEmail, AdminPassword);
        using var client = CreateAuthenticatedClient(token.AccessToken);

        var response = await client.DeleteAsync($"/api/v1/auth/pats/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RevokePat_OwnPat_Returns204()
    {
        var token = await GetTokenAsync(AdminEmail, AdminPassword);
        using var client = CreateAuthenticatedClient(token.AccessToken);

        var createResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/pats", new PatCreateRequest("PAT to Revoke"));
        var created = await createResponse.Content.ReadFromJsonAsync<PatCreateResponse>(JsonOptions);

        var revokeResponse = await client.DeleteAsync($"/api/v1/auth/pats/{created!.Id}");

        revokeResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task RevokePat_AlreadyRevoked_Returns404()
    {
        var token = await GetTokenAsync(AdminEmail, AdminPassword);
        using var client = CreateAuthenticatedClient(token.AccessToken);

        var createResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/pats", new PatCreateRequest("PAT to Double-Revoke"));
        var created = await createResponse.Content.ReadFromJsonAsync<PatCreateResponse>(JsonOptions);

        await client.DeleteAsync($"/api/v1/auth/pats/{created!.Id}");
        var secondRevoke = await client.DeleteAsync($"/api/v1/auth/pats/{created.Id}");

        secondRevoke.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RevokePat_RevokedPatCanNoLongerAuthenticate()
    {
        var token = await GetTokenAsync(AdminEmail, AdminPassword);
        using var jwtClient = CreateAuthenticatedClient(token.AccessToken);

        var createResponse = await jwtClient.PostAsJsonAsync(
            "/api/v1/auth/pats", new PatCreateRequest("PAT Revoked Auth Test"));
        var created = await createResponse.Content.ReadFromJsonAsync<PatCreateResponse>(JsonOptions);

        // Confirm it works before revocation
        using var patClient = _factory.CreateClient();
        patClient.DefaultRequestHeaders.Add("X-Api-Key", created!.Token);
        var before = await patClient.GetAsync("/api/v1/auth/pats");
        before.StatusCode.Should().Be(HttpStatusCode.OK);

        // Revoke
        await jwtClient.DeleteAsync($"/api/v1/auth/pats/{created.Id}");

        // Should no longer authenticate
        var after = await patClient.GetAsync("/api/v1/auth/pats");
        after.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── GET /api/v1/auth/users ───────────────────────────────────────────

    [Fact]
    public async Task ListUsers_Unauthenticated_Returns401()
    {
        var response = await _anonClient.GetAsync("/api/v1/auth/users");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListUsers_AsViewer_Returns403()
    {
        var token = await GetTokenAsync(ViewerEmail, ViewerPassword);
        using var client = CreateAuthenticatedClient(token.AccessToken);

        var response = await client.GetAsync("/api/v1/auth/users");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ListUsers_AsAdmin_Returns200WithUsers()
    {
        var token = await GetTokenAsync(AdminEmail, AdminPassword);
        using var client = CreateAuthenticatedClient(token.AccessToken);

        var response = await client.GetAsync("/api/v1/auth/users");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var users = await response.Content.ReadFromJsonAsync<List<UserListItem>>(JsonOptions);
        users.Should().NotBeNull();
        users.Should().Contain(u => u.Email == AdminEmail);
    }

    [Fact]
    public async Task ListUsers_AsAdmin_ReturnsRolesForEachUser()
    {
        var token = await GetTokenAsync(AdminEmail, AdminPassword);
        using var client = CreateAuthenticatedClient(token.AccessToken);

        var response = await client.GetAsync("/api/v1/auth/users");
        var users = await response.Content.ReadFromJsonAsync<List<UserListItem>>(JsonOptions);

        var admin = users!.First(u => u.Email == AdminEmail);
        admin.Roles.Should().Contain("Owner");
        admin.Roles.Should().Contain("Admin");
    }

    // ── PUT /api/v1/auth/users/{id}/roles ────────────────────────────────

    [Fact]
    public async Task AssignRoles_Unauthenticated_Returns401()
    {
        var response = await _anonClient.PutAsJsonAsync(
            $"/api/v1/auth/users/{Guid.NewGuid()}/roles",
            new AssignRolesRequest(["Viewer"]));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AssignRoles_AsViewer_Returns403()
    {
        var token = await GetTokenAsync(ViewerEmail, ViewerPassword);
        using var client = CreateAuthenticatedClient(token.AccessToken);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/auth/users/{Guid.NewGuid()}/roles",
            new AssignRolesRequest(["Editor"]));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AssignRoles_NonExistentUser_Returns404()
    {
        var token = await GetTokenAsync(AdminEmail, AdminPassword);
        using var client = CreateAuthenticatedClient(token.AccessToken);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/auth/users/{Guid.NewGuid()}/roles",
            new AssignRolesRequest(["Viewer"]));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AssignRoles_AssignOwnerRole_Returns400()
    {
        var token = await GetTokenAsync(AdminEmail, AdminPassword);
        using var client = CreateAuthenticatedClient(token.AccessToken);

        var viewerId = await GetUserIdAsync(client, ViewerEmail);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/auth/users/{viewerId}/roles",
            new AssignRolesRequest(["Owner"]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AssignRoles_ValidRoleChange_Returns204()
    {
        var token = await GetTokenAsync(AdminEmail, AdminPassword);
        using var client = CreateAuthenticatedClient(token.AccessToken);

        var viewerId = await GetUserIdAsync(client, ViewerEmail);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/auth/users/{viewerId}/roles",
            new AssignRolesRequest(["Editor"]));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task AssignRoles_OwnerRolePreservedWhenNotRequested()
    {
        var token = await GetTokenAsync(AdminEmail, AdminPassword);
        using var client = CreateAuthenticatedClient(token.AccessToken);

        var adminId = await GetUserIdAsync(client, AdminEmail);

        // Assign only "Admin" — the Owner role should not be stripped
        var response = await client.PutAsJsonAsync(
            $"/api/v1/auth/users/{adminId}/roles",
            new AssignRolesRequest(["Admin"]));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify Owner role is still present
        var usersResponse = await client.GetAsync("/api/v1/auth/users");
        var users = await usersResponse.Content.ReadFromJsonAsync<List<UserListItem>>(JsonOptions);
        var adminUser = users!.First(u => u.Email == AdminEmail);
        adminUser.Roles.Should().Contain("Owner");
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private static async Task<Guid> GetUserIdAsync(HttpClient adminClient, string email)
    {
        var response = await adminClient.GetAsync("/api/v1/auth/users");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var users = await response.Content.ReadFromJsonAsync<List<UserListItem>>(JsonOptions);
        return users!.First(u => u.Email == email).Id;
    }
}
