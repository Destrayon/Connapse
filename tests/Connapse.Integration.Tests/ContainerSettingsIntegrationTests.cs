using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for GET/PUT /api/containers/{id}/settings — per-container settings overrides.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class ContainerSettingsIntegrationTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task GetContainerSettings_NewContainer_ReturnsEmptyOverrides()
    {
        var createResponse = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "settings-empty-test" });
        var container = await createResponse.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);

        var response = await fixture.AdminClient.GetAsync(
            $"/api/containers/{container!.Id}/settings");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var overrides = await response.Content.ReadFromJsonAsync<ContainerSettingsOverrides>(JsonOptions);
        overrides.Should().NotBeNull();
        overrides!.Chunking.Should().BeNull();
        overrides.Embedding.Should().BeNull();
        overrides.Search.Should().BeNull();
        overrides.Upload.Should().BeNull();

        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    [Fact]
    public async Task SaveContainerSettings_EmbeddingOverride_PersistsAndRetrieves()
    {
        var createResponse = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "settings-embed-test" });
        var container = await createResponse.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);

        var overrides = new ContainerSettingsOverrides
        {
            Embedding = new EmbeddingSettings
            {
                Provider = "OpenAI",
                Model = "text-embedding-3-small",
                Dimensions = 1536
            }
        };

        var putResponse = await fixture.AdminClient.PutAsJsonAsync(
            $"/api/containers/{container!.Id}/settings", overrides);
        putResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var getResponse = await fixture.AdminClient.GetAsync(
            $"/api/containers/{container.Id}/settings");
        var retrieved = await getResponse.Content.ReadFromJsonAsync<ContainerSettingsOverrides>(JsonOptions);

        retrieved.Should().NotBeNull();
        retrieved!.Embedding.Should().NotBeNull();
        retrieved.Embedding!.Provider.Should().Be("OpenAI");
        retrieved.Embedding.Model.Should().Be("text-embedding-3-small");
        retrieved.Embedding.Dimensions.Should().Be(1536);
        retrieved.Chunking.Should().BeNull("only embedding was overridden");

        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    [Fact]
    public async Task SaveContainerSettings_ResetToEmpty_ClearsOverrides()
    {
        var createResponse = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "settings-reset-test" });
        var container = await createResponse.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);

        // Set an override
        var overrides = new ContainerSettingsOverrides
        {
            Search = new SearchSettings { Mode = "Keyword", TopK = 5 }
        };
        await fixture.AdminClient.PutAsJsonAsync(
            $"/api/containers/{container!.Id}/settings", overrides);

        // Clear it
        var empty = new ContainerSettingsOverrides();
        await fixture.AdminClient.PutAsJsonAsync(
            $"/api/containers/{container.Id}/settings", empty);

        var getResponse = await fixture.AdminClient.GetAsync(
            $"/api/containers/{container.Id}/settings");
        var retrieved = await getResponse.Content.ReadFromJsonAsync<ContainerSettingsOverrides>(JsonOptions);

        retrieved!.Search.Should().BeNull("overrides were cleared");

        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    [Fact]
    public async Task GetContainerSettings_NonExistentContainer_Returns404()
    {
        var fakeId = Guid.NewGuid();
        var response = await fixture.AdminClient.GetAsync($"/api/containers/{fakeId}/settings");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SaveContainerSettings_NonExistentContainer_Returns404()
    {
        var fakeId = Guid.NewGuid();
        var overrides = new ContainerSettingsOverrides
        {
            Chunking = new ChunkingSettings { Strategy = "FixedSize" }
        };

        var response = await fixture.AdminClient.PutAsJsonAsync(
            $"/api/containers/{fakeId}/settings", overrides);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // DTOs
    private record ContainerDto(string Id, string Name);
}
