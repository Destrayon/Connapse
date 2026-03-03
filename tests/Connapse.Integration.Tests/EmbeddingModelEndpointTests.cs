using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for embedding model discovery and reindex status endpoints.
/// These endpoints don't require Ollama — they work with an empty database.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class EmbeddingModelEndpointTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task GetGlobalEmbeddingModels_EmptyDb_ReturnsCurrentModelWithEmptyList()
    {
        var response = await fixture.AdminClient.GetAsync("/api/settings/embedding-models");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<EmbeddingModelsResponse>(JsonOptions);
        result.Should().NotBeNull();
        result!.CurrentModel.Should().NotBeNullOrWhiteSpace("should reflect configured model");
    }

    [Fact]
    public async Task GetReindexStatus_ReturnsQueueDepth()
    {
        var response = await fixture.AdminClient.GetAsync("/api/settings/reindex/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ReindexStatusResponse>(JsonOptions);
        result.Should().NotBeNull();
        result!.QueueDepth.Should().BeGreaterThanOrEqualTo(0);
    }

    // DTOs
    private record EmbeddingModelsResponse(string CurrentModel);
    private record ReindexStatusResponse(int QueueDepth, bool IsActive);
}
