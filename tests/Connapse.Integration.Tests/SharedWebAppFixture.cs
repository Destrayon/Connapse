using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;

namespace Connapse.Integration.Tests;

/// <summary>
/// Shared fixture for all integration tests. Creates a single PostgreSQL + MinIO container pair
/// and a single WebApplicationFactory instance, shared across all test classes in the collection.
/// This dramatically reduces test suite startup time compared to per-class container setup.
/// </summary>
public sealed class SharedWebAppFixture : IAsyncLifetime
{
    public const string AdminEmail = "admin@integration-tests.connapse.io";
    public const string AdminPassword = "SharedAdminTest1!";
    public const string TestJwtSecret = "shared-test-jwt-secret-for-integration-tests-64chars!!";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("connapse_integration_test")
        .WithUsername("integration_test")
        .WithPassword("integration_test")
        .Build();

    private readonly MinioContainer _minio = new MinioBuilder()
        .WithImage("minio/minio")
        .Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public HttpClient AdminClient { get; private set; } = null!;
    public string AdminToken { get; private set; } = null!;

    /// <summary>The MinIO host:port string used in connection test requests.</summary>
    public string MinioHostPort { get; private set; } = null!;

    public string MinioAccessKey => MinioBuilder.DefaultUsername;
    public string MinioSecretKey => MinioBuilder.DefaultPassword;

    public async Task InitializeAsync()
    {
        // Start both containers in parallel — saves ~5-10 seconds vs sequential
        await Task.WhenAll(_postgres.StartAsync(), _minio.StartAsync());

        MinioHostPort = $"{_minio.Hostname}:{_minio.GetMappedPublicPort(9000)}";

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:DefaultConnection", _postgres.GetConnectionString());
                builder.UseSetting("Knowledge:Storage:MinIO:Endpoint", MinioHostPort);
                builder.UseSetting("Knowledge:Storage:MinIO:AccessKey", MinioBuilder.DefaultUsername);
                builder.UseSetting("Knowledge:Storage:MinIO:SecretKey", MinioBuilder.DefaultPassword);
                builder.UseSetting("Knowledge:Storage:MinIO:UseSSL", "false");
                builder.UseSetting("Knowledge:Chunking:MaxChunkSize", "200");
                builder.UseSetting("Knowledge:Chunking:MinChunkSize", "10");
                builder.UseSetting("Knowledge:Chunking:Overlap", "20");
                builder.UseSetting("Knowledge:Upload:ParallelWorkers", "1");
                builder.UseSetting("CONNAPSE_ADMIN_EMAIL", AdminEmail);
                builder.UseSetting("CONNAPSE_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("Identity:Jwt:Secret", TestJwtSecret);
            });

        AdminClient = Factory.CreateClient();

        // Poll /health until the app is ready — replaces hard-coded Task.Delay(2000)
        await WaitForAppReadyAsync(AdminClient);

        AdminToken = await GetAdminTokenAsync(AdminClient);
        AdminClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AdminToken);
    }

    public async Task DisposeAsync()
    {
        AdminClient.Dispose();
        await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
        await _minio.DisposeAsync();
    }

    private static async Task WaitForAppReadyAsync(HttpClient client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await client.GetAsync("/health");
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch { /* app not ready yet */ }
            await Task.Delay(200);
        }
        throw new TimeoutException("App did not become ready within 30 seconds");
    }

    private static async Task<string> GetAdminTokenAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/token", new LoginRequest(AdminEmail, AdminPassword));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions);
        return token!.AccessToken;
    }
}
