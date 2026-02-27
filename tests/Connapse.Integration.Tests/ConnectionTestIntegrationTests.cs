using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for connection testing API endpoints.
/// Tests the /api/settings/test-connection endpoint with real services.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class ConnectionTestIntegrationTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task TestConnection_MinioWithValidCredentials_ReturnsSuccess()
    {
        // Arrange
        var request = new
        {
            Category = "storage",
            Settings = new StorageSettings
            {
                MinioEndpoint = fixture.MinioHostPort,
                MinioAccessKey = fixture.MinioAccessKey,
                MinioSecretKey = fixture.MinioSecretKey,
                MinioBucketName = "test-bucket",
                MinioUseSSL = false
            },
            TimeoutSeconds = 10
        };

        // Act
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/settings/test-connection", request);

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
                MinioEndpoint = fixture.MinioHostPort,
                MinioAccessKey = "invalid_key",
                MinioSecretKey = "invalid_secret",
                MinioBucketName = "test-bucket",
                MinioUseSSL = false
            },
            TimeoutSeconds = 10
        };

        // Act
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/settings/test-connection", request);

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
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/settings/test-connection", request);

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
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/settings/test-connection", request);

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
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/settings/test-connection", request);

        // Assert - Expect 400 Bad Request for missing required field
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
