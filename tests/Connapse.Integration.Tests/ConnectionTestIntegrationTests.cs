using System.Net;
using System.Net.Http.Json;
using Connapse.Core;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for connection testing API endpoints.
/// Tests the /api/settings/test-connection endpoint with real services.
/// </summary>
[Trait("Category", "Integration")]
public class ConnectionTestIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgresContainer = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("aikp_test")
        .WithUsername("aikp_test")
        .WithPassword("aikp_test")
        .Build();

    private readonly MinioContainer _minioContainer = new MinioBuilder()
        .WithImage("minio/minio")
        .Build();

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _postgresContainer.StartAsync();
        await _minioContainer.StartAsync();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:DefaultConnection", _postgresContainer.GetConnectionString());
                builder.UseSetting("Knowledge:Storage:MinIO:Endpoint", $"{_minioContainer.Hostname}:{_minioContainer.GetMappedPublicPort(9000)}");
                builder.UseSetting("Knowledge:Storage:MinIO:AccessKey", MinioBuilder.DefaultUsername);
                builder.UseSetting("Knowledge:Storage:MinIO:SecretKey", MinioBuilder.DefaultPassword);
                builder.UseSetting("Knowledge:Storage:MinIO:UseSSL", "false");
            });

        _client = _factory.CreateClient();
        await Task.Delay(2000); // Allow services to initialize
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _postgresContainer.DisposeAsync();
        await _minioContainer.DisposeAsync();
    }

    [Fact]
    public async Task TestConnection_MinioWithValidCredentials_ReturnsSuccess()
    {
        // Arrange
        var request = new
        {
            Category = "storage",
            Settings = new StorageSettings
            {
                MinioEndpoint = $"{_minioContainer.Hostname}:{_minioContainer.GetMappedPublicPort(9000)}",
                MinioAccessKey = MinioBuilder.DefaultUsername,
                MinioSecretKey = MinioBuilder.DefaultPassword,
                MinioBucketName = "test-bucket",
                MinioUseSSL = false
            },
            TimeoutSeconds = 10
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/settings/test-connection", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ConnectionTestResult>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Message.Should().Contain("Connected to MinIO");
        result.Details.Should().ContainKey("bucketExists");
    }

    [Fact]
    public async Task TestConnection_MinioWithInvalidCredentials_ReturnsFailure()
    {
        // Arrange
        var request = new
        {
            Category = "storage",
            Settings = new StorageSettings
            {
                MinioEndpoint = $"{_minioContainer.Hostname}:{_minioContainer.GetMappedPublicPort(9000)}",
                MinioAccessKey = "invalid_key",
                MinioSecretKey = "invalid_secret",
                MinioBucketName = "test-bucket",
                MinioUseSSL = false
            },
            TimeoutSeconds = 10
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/settings/test-connection", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ConnectionTestResult>();
        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.Message.Should().Contain("Access denied");
    }

    [Fact]
    public async Task TestConnection_OllamaUnavailable_ReturnsFailure()
    {
        // Arrange - Use a non-existent Ollama endpoint
        var request = new
        {
            Category = "embedding",
            Settings = new EmbeddingSettings
            {
                BaseUrl = "http://localhost:54321", // Non-existent endpoint (valid port range)
                Model = "test-model"
            },
            TimeoutSeconds = 5
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/settings/test-connection", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ConnectionTestResult>();
        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.Message.Should().Match(m =>
            m.Contains("Connection failed", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("error", StringComparison.OrdinalIgnoreCase),
            "failure message should indicate connection error");
    }

    [Fact]
    public async Task TestConnection_InvalidCategory_ReturnsBadRequest()
    {
        // Arrange
        var request = new
        {
            Category = "unknown_category",
            Settings = new { },
            TimeoutSeconds = 10
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/settings/test-connection", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ConnectionTestResult>();
        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.Message.Should().Contain("does not support connection testing");
    }

    [Fact]
    public async Task TestConnection_MissingSettings_ReturnsBadRequest()
    {
        // Arrange
        var request = new
        {
            Category = "embedding"
            // Missing Settings property
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/settings/test-connection", request);

        // Assert - Expect 400 Bad Request for missing required field
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
