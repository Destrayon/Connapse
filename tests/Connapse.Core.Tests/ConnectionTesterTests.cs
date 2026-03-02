using System.Net;
using System.Text.Json;
using Connapse.Core;
using Connapse.Storage.ConnectionTesters;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Connapse.Core.Tests;

/// <summary>
/// Unit tests for connection testers (OllamaConnectionTester, MinioConnectionTester).
/// Uses mocked HTTP responses to test logic without requiring real services.
/// </summary>
[Trait("Category", "Unit")]
public class OllamaConnectionTesterTests
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OllamaConnectionTester> _logger;
    private readonly HttpMessageHandlerStub _httpHandler;

    public OllamaConnectionTesterTests()
    {
        _httpHandler = new HttpMessageHandlerStub();
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _httpClientFactory.CreateClient().Returns(new HttpClient(_httpHandler));
        _logger = Substitute.For<ILogger<OllamaConnectionTester>>();
    }

    [Fact]
    public async Task TestConnectionAsync_ValidOllamaResponse_ReturnsSuccess()
    {
        // Arrange
        var tester = new OllamaConnectionTester(_httpClientFactory, _logger);
        var settings = new EmbeddingSettings { BaseUrl = "http://localhost:11434" };

        var ollamaResponse = new
        {
            models = new[]
            {
                new { name = "nomic-embed-text", modified_at = "2024-01-01T00:00:00Z", size = 274000000L },
                new { name = "llama3.2", modified_at = "2024-01-02T00:00:00Z", size = 2000000000L }
            }
        };

        _httpHandler.ResponseContent = JsonSerializer.Serialize(ollamaResponse);
        _httpHandler.ResponseStatusCode = HttpStatusCode.OK;

        // Act
        var result = await tester.TestConnectionAsync(settings);

        // Assert
        result.Success.Should().BeTrue();
        result.Message.Should().Contain("Connected to Ollama");
        result.Message.Should().Contain("2 models available");
        result.Details.Should().ContainKey("modelCount");
        result.Details!["modelCount"].Should().Be(2);
    }

    [Fact]
    public async Task TestConnectionAsync_EmptyModelsList_ReturnsSuccessWithNoModels()
    {
        // Arrange
        var tester = new OllamaConnectionTester(_httpClientFactory, _logger);
        var settings = new EmbeddingSettings { BaseUrl = "http://localhost:11434" };

        var ollamaResponse = new { models = Array.Empty<object>() };
        _httpHandler.ResponseContent = JsonSerializer.Serialize(ollamaResponse);
        _httpHandler.ResponseStatusCode = HttpStatusCode.OK;

        // Act
        var result = await tester.TestConnectionAsync(settings);

        // Assert
        result.Success.Should().BeTrue();
        result.Message.Should().Contain("no models available");
        result.Details.Should().ContainKey("modelCount");
        result.Details!["modelCount"].Should().Be(0);
    }

    [Fact]
    public async Task TestConnectionAsync_MissingBaseUrl_ReturnsFailure()
    {
        // Arrange
        var tester = new OllamaConnectionTester(_httpClientFactory, _logger);
        var settings = new EmbeddingSettings { BaseUrl = null };

        // Act
        var result = await tester.TestConnectionAsync(settings);

        // Assert
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("BaseUrl is required");
    }

    [Fact]
    public async Task TestConnectionAsync_ConnectionRefused_ReturnsFailure()
    {
        // Arrange
        var tester = new OllamaConnectionTester(_httpClientFactory, _logger);
        var settings = new EmbeddingSettings { BaseUrl = "http://localhost:11434" };

        _httpHandler.ThrowException = new HttpRequestException("Connection refused");

        // Act
        var result = await tester.TestConnectionAsync(settings);

        // Assert
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Connection failed");
        result.Message.Should().Contain("Connection refused");
    }

    [Fact]
    public async Task TestConnectionAsync_Timeout_ReturnsFailureWithTimeoutMessage()
    {
        // Arrange
        var tester = new OllamaConnectionTester(_httpClientFactory, _logger);
        var settings = new EmbeddingSettings { BaseUrl = "http://localhost:11434" };

        _httpHandler.ThrowException = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");

        // Act
        var result = await tester.TestConnectionAsync(settings, timeout: TimeSpan.FromSeconds(5));

        // Assert
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("timed out");
        result.Details.Should().ContainKey("timeoutSeconds");
    }

    [Fact]
    public async Task TestConnectionAsync_InvalidJson_ReturnsFailure()
    {
        // Arrange
        var tester = new OllamaConnectionTester(_httpClientFactory, _logger);
        var settings = new EmbeddingSettings { BaseUrl = "http://localhost:11434" };

        _httpHandler.ResponseContent = "Invalid JSON {{{";
        _httpHandler.ResponseStatusCode = HttpStatusCode.OK;

        // Act
        var result = await tester.TestConnectionAsync(settings);

        // Assert
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("error");
    }
}

[Trait("Category", "Unit")]
public class MinioConnectionTesterTests
{
    private readonly ILogger<MinioConnectionTester> _logger;

    public MinioConnectionTesterTests()
    {
        _logger = Substitute.For<ILogger<MinioConnectionTester>>();
    }

    [Fact]
    public async Task TestConnectionAsync_MissingEndpoint_ReturnsFailure()
    {
        // Arrange
        var tester = new MinioConnectionTester(_logger);
        var settings = new Connapse.Storage.FileSystem.MinioOptions { Endpoint = null! };

        // Act
        var result = await tester.TestConnectionAsync(settings);

        // Assert
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("MinioEndpoint is required");
    }

    [Fact]
    public async Task TestConnectionAsync_MissingCredentials_ReturnsFailure()
    {
        // Arrange
        var tester = new MinioConnectionTester(_logger);
        var settings = new Connapse.Storage.FileSystem.MinioOptions
        {
            Endpoint = "localhost:9000",
            AccessKey = null!,
            SecretKey = null!
        };

        // Act
        var result = await tester.TestConnectionAsync(settings);

        // Assert
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("MinioAccessKey and MinioSecretKey are required");
    }

    // Note: Full MinIO integration tests require real service and are covered in Integration.Tests
}

[Trait("Category", "Unit")]
public class AzureAdConnectionTesterTests
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AzureAdConnectionTester> _logger;
    private readonly HttpMessageHandlerStub _httpHandler;

    public AzureAdConnectionTesterTests()
    {
        _httpHandler = new HttpMessageHandlerStub();
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _httpClientFactory.CreateClient().Returns(new HttpClient(_httpHandler));
        _logger = Substitute.For<ILogger<AzureAdConnectionTester>>();
    }

    [Fact]
    public async Task TestConnectionAsync_MissingTenantId_ReturnsFailure()
    {
        var tester = new AzureAdConnectionTester(_httpClientFactory, _logger);
        var settings = new AzureAdSettings { ClientId = "test-id", TenantId = "" };

        var result = await tester.TestConnectionAsync(settings);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Tenant ID is required");
    }

    [Fact]
    public async Task TestConnectionAsync_MissingClientId_ReturnsFailure()
    {
        var tester = new AzureAdConnectionTester(_httpClientFactory, _logger);
        var settings = new AzureAdSettings { ClientId = "", TenantId = "test-tenant" };

        var result = await tester.TestConnectionAsync(settings);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Client ID is required");
    }

    [Fact]
    public async Task TestConnectionAsync_ValidTenant_ReturnsSuccess()
    {
        var tester = new AzureAdConnectionTester(_httpClientFactory, _logger);
        var settings = new AzureAdSettings
        {
            ClientId = "00000000-0000-0000-0000-000000000001",
            TenantId = "00000000-0000-0000-0000-000000000002"
        };

        var metadata = new
        {
            issuer = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000002/v2.0",
            token_endpoint = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000002/oauth2/v2.0/token"
        };
        _httpHandler.ResponseContent = JsonSerializer.Serialize(metadata);
        _httpHandler.ResponseStatusCode = HttpStatusCode.OK;

        var result = await tester.TestConnectionAsync(settings);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("Connected to Azure AD tenant");
        result.Details.Should().ContainKey("issuer");
        result.Details!["tenantId"].Should().Be("00000000-0000-0000-0000-000000000002");
    }

    [Fact]
    public async Task TestConnectionAsync_InvalidTenant_ReturnsFailure()
    {
        var tester = new AzureAdConnectionTester(_httpClientFactory, _logger);
        var settings = new AzureAdSettings
        {
            ClientId = "test-id",
            TenantId = "invalid-tenant"
        };

        _httpHandler.ResponseStatusCode = HttpStatusCode.BadRequest;
        _httpHandler.ResponseContent = "{ \"error\": \"invalid_tenant\" }";

        var result = await tester.TestConnectionAsync(settings);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("400");
        result.Message.Should().Contain("verify the Tenant ID");
    }
}

/// <summary>
/// HTTP message handler stub for mocking HTTP responses in tests.
/// </summary>
internal class HttpMessageHandlerStub : HttpMessageHandler
{
    public string ResponseContent { get; set; } = string.Empty;
    public HttpStatusCode ResponseStatusCode { get; set; } = HttpStatusCode.OK;
    public Exception? ThrowException { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (ThrowException != null)
        {
            throw ThrowException;
        }

        var response = new HttpResponseMessage(ResponseStatusCode)
        {
            Content = new StringContent(ResponseContent)
        };

        return Task.FromResult(response);
    }
}
