using System.Net;
using System.Net.Http.Json;
using Connapse.Core;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for runtime-mutable settings with IOptionsMonitor live reload.
/// </summary>
public class SettingsIntegrationTests : IAsyncLifetime
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
        await Task.Delay(2000);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _postgresContainer.DisposeAsync();
        await _minioContainer.DisposeAsync();
    }

    [Fact]
    public async Task GetSettings_EmbeddingSettings_ReturnsCurrentValues()
    {
        // Act
        var response = await _client.GetAsync("/api/settings/embedding");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var settings = await response.Content.ReadFromJsonAsync<EmbeddingSettings>();
        settings.Should().NotBeNull();
        settings!.Provider.Should().NotBeNullOrWhiteSpace();
        settings.Model.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task UpdateSettings_ChunkingSettings_LiveReloadWorks()
    {
        // Arrange: Get current chunking settings
        var getResponse = await _client.GetAsync("/api/settings/chunking");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var originalSettings = await getResponse.Content.ReadFromJsonAsync<ChunkingSettings>();
        originalSettings.Should().NotBeNull();

        var originalMaxChunkSize = originalSettings!.MaxChunkSize;

        // Act: Update chunking settings
        var newSettings = originalSettings with
        {
            Strategy = "FixedSize",
            MaxChunkSize = originalMaxChunkSize + 100 // Change the value
        };

        var updateResponse = await _client.PutAsJsonAsync("/api/settings/chunking", newSettings);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Give a moment for IOptionsMonitor to propagate the change
        await Task.Delay(1000);

        // Assert: Verify settings were updated
        var verifyResponse = await _client.GetAsync("/api/settings/chunking");
        verifyResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var updatedSettings = await verifyResponse.Content.ReadFromJsonAsync<ChunkingSettings>();
        updatedSettings.Should().NotBeNull();
        updatedSettings!.MaxChunkSize.Should().Be(originalMaxChunkSize + 100, "Settings should be updated immediately");

        // Cleanup: Restore original settings
        await _client.PutAsJsonAsync("/api/settings/chunking", originalSettings);
    }

    [Fact]
    public async Task UpdateSettings_SearchSettings_LiveReloadWorks()
    {
        // Arrange: Get current search settings
        var getResponse = await _client.GetAsync("/api/settings/search");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var originalSettings = await getResponse.Content.ReadFromJsonAsync<SearchSettings>();
        originalSettings.Should().NotBeNull();

        // Act: Toggle search mode
        var newMode = originalSettings!.Mode == "Hybrid" ? "Semantic" : "Hybrid";
        var newSettings = originalSettings with
        {
            Mode = newMode
        };

        var updateResponse = await _client.PutAsJsonAsync("/api/settings/search", newSettings);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        await Task.Delay(1000);

        // Assert: Verify settings were updated
        var verifyResponse = await _client.GetAsync("/api/settings/search");
        verifyResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var updatedSettings = await verifyResponse.Content.ReadFromJsonAsync<SearchSettings>();
        updatedSettings.Should().NotBeNull();
        updatedSettings!.Mode.Should().Be(newMode, "Search mode should be updated immediately");

        // Cleanup
        await _client.PutAsJsonAsync("/api/settings/search", originalSettings);
    }

    [Fact]
    public async Task UpdateSettings_MultipleCategories_IndependentlyUpdateable()
    {
        // Arrange: Get settings from multiple categories
        var embeddingResponse = await _client.GetAsync("/api/settings/embedding");
        var chunkingResponse = await _client.GetAsync("/api/settings/chunking");

        embeddingResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        chunkingResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var originalEmbedding = await embeddingResponse.Content.ReadFromJsonAsync<EmbeddingSettings>();
        var originalChunking = await chunkingResponse.Content.ReadFromJsonAsync<ChunkingSettings>();

        // Act: Update both categories
        var newEmbedding = originalEmbedding! with { Model = "test-model-v2" };
        var newChunking = originalChunking! with { MaxChunkSize = 999 };

        await _client.PutAsJsonAsync("/api/settings/embedding", newEmbedding);
        await _client.PutAsJsonAsync("/api/settings/chunking", newChunking);

        await Task.Delay(1000);

        // Assert: Both updates are reflected
        var verifyEmbedding = await (await _client.GetAsync("/api/settings/embedding"))
            .Content.ReadFromJsonAsync<EmbeddingSettings>();
        var verifyChunking = await (await _client.GetAsync("/api/settings/chunking"))
            .Content.ReadFromJsonAsync<ChunkingSettings>();

        verifyEmbedding!.Model.Should().Be("test-model-v2");
        verifyChunking!.MaxChunkSize.Should().Be(999);

        // Cleanup
        await _client.PutAsJsonAsync("/api/settings/embedding", originalEmbedding);
        await _client.PutAsJsonAsync("/api/settings/chunking", originalChunking);
    }
}
