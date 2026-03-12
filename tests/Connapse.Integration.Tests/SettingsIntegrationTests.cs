using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for runtime-mutable settings with IOptionsMonitor live reload.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SettingsIntegrationTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task GetSettings_EmbeddingSettings_ReturnsCurrentValues()
    {
        // Act
        var response = await fixture.AdminClient.GetAsync("/api/settings/embedding");

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
        var getResponse = await fixture.AdminClient.GetAsync("/api/settings/chunking");
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

        var updateResponse = await fixture.AdminClient.PutAsJsonAsync("/api/settings/chunking", newSettings);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Give a moment for IOptionsMonitor to propagate the change
        await Task.Delay(1000);

        // Assert: Verify settings were updated
        var verifyResponse = await fixture.AdminClient.GetAsync("/api/settings/chunking");
        verifyResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var updatedSettings = await verifyResponse.Content.ReadFromJsonAsync<ChunkingSettings>();
        updatedSettings.Should().NotBeNull();
        updatedSettings!.MaxChunkSize.Should().Be(originalMaxChunkSize + 100, "Settings should be updated immediately");

        // Cleanup: Restore original settings
        await fixture.AdminClient.PutAsJsonAsync("/api/settings/chunking", originalSettings);
    }

    [Fact]
    public async Task UpdateSettings_SearchSettings_LiveReloadWorks()
    {
        // Arrange: Get current search settings
        var getResponse = await fixture.AdminClient.GetAsync("/api/settings/search");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var originalSettings = await getResponse.Content.ReadFromJsonAsync<SearchSettings>();
        originalSettings.Should().NotBeNull();

        // Act: Toggle search mode
        var newMode = originalSettings!.Mode == "Hybrid" ? "Semantic" : "Hybrid";
        var newSettings = originalSettings with
        {
            Mode = newMode
        };

        var updateResponse = await fixture.AdminClient.PutAsJsonAsync("/api/settings/search", newSettings);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        await Task.Delay(1000);

        // Assert: Verify settings were updated
        var verifyResponse = await fixture.AdminClient.GetAsync("/api/settings/search");
        verifyResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var updatedSettings = await verifyResponse.Content.ReadFromJsonAsync<SearchSettings>();
        updatedSettings.Should().NotBeNull();
        updatedSettings!.Mode.Should().Be(newMode, "Search mode should be updated immediately");

        // Cleanup
        await fixture.AdminClient.PutAsJsonAsync("/api/settings/search", originalSettings);
    }

    [Fact]
    public async Task UpdateSettings_MultipleCategories_IndependentlyUpdateable()
    {
        // Arrange: Get settings from multiple categories
        var embeddingResponse = await fixture.AdminClient.GetAsync("/api/settings/embedding");
        var chunkingResponse = await fixture.AdminClient.GetAsync("/api/settings/chunking");

        embeddingResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        chunkingResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var originalEmbedding = await embeddingResponse.Content.ReadFromJsonAsync<EmbeddingSettings>();
        var originalChunking = await chunkingResponse.Content.ReadFromJsonAsync<ChunkingSettings>();

        // Act: Update both categories
        var newEmbedding = originalEmbedding! with { Model = "test-model-v2" };
        var newChunking = originalChunking! with { MaxChunkSize = 999 };

        await fixture.AdminClient.PutAsJsonAsync("/api/settings/embedding", newEmbedding);
        await fixture.AdminClient.PutAsJsonAsync("/api/settings/chunking", newChunking);

        await Task.Delay(1000);

        // Assert: Both updates are reflected
        var verifyEmbedding = await (await fixture.AdminClient.GetAsync("/api/settings/embedding"))
            .Content.ReadFromJsonAsync<EmbeddingSettings>();
        var verifyChunking = await (await fixture.AdminClient.GetAsync("/api/settings/chunking"))
            .Content.ReadFromJsonAsync<ChunkingSettings>();

        verifyEmbedding!.Model.Should().Be("test-model-v2");
        verifyChunking!.MaxChunkSize.Should().Be(999);

        // Cleanup
        await fixture.AdminClient.PutAsJsonAsync("/api/settings/embedding", originalEmbedding);
        await fixture.AdminClient.PutAsJsonAsync("/api/settings/chunking", originalChunking);
    }

    [Fact]
    public async Task GetSettings_UnknownCategory_Returns404()
    {
        // Act
        var response = await fixture.AdminClient.GetAsync("/api/settings/nonexistent");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateSettings_UnknownCategory_Returns404()
    {
        // Act
        var response = await fixture.AdminClient.PutAsJsonAsync("/api/settings/nonexistent", new { foo = "bar" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
