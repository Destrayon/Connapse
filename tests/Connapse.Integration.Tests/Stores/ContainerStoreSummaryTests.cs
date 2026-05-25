using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests.Stores;

/// <summary>
/// Verifies that UpdateSummaryAsync persists summary fields on IContainerStore
/// and that GetAsync returns the stored values.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class ContainerStoreSummaryTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task UpdateSummaryAsync_PersistsSummary()
    {
        // Arrange: create a container via the HTTP API to keep tests consistent with the collection pattern
        var createResponse = await fixture.AdminClient.PostAsJsonAsync(
            "/api/containers", new { Name = "summary-store-test" });
        createResponse.EnsureSuccessStatusCode();

        var created = await createResponse.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);
        created.Should().NotBeNull();

        var containerGuid = Guid.Parse(created!.Id);

        // Resolve IContainerStore directly from the DI container
        using var scope = fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IContainerStore>();

        var summary = "This container holds AI research papers organized by topic.";
        var generatedAt = DateTime.UtcNow;
        string hash = new string('a', 64);

        // Act
        await store.UpdateSummaryAsync(containerGuid, summary, generatedAt, hash);

        // Assert
        Container? reloaded = await store.GetAsync(containerGuid);
        reloaded.Should().NotBeNull();
        reloaded!.Summary.Should().Be(summary);
        reloaded.SummaryGeneratedAt.Should().BeCloseTo(generatedAt, TimeSpan.FromSeconds(1));

        // Cleanup
        await fixture.AdminClient.DeleteAsync($"/api/containers/{created.Id}");
    }

    [Fact]
    public async Task UpdateSummaryAsync_NullValues_ClearsSummary()
    {
        // Arrange: create container and set a summary first
        var createResponse = await fixture.AdminClient.PostAsJsonAsync(
            "/api/containers", new { Name = "summary-clear-test" });
        createResponse.EnsureSuccessStatusCode();

        var created = await createResponse.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);
        var containerGuid = Guid.Parse(created!.Id);

        using var scope = fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IContainerStore>();

        await store.UpdateSummaryAsync(containerGuid, "initial summary", DateTime.UtcNow, new string('b', 64));

        // Act: clear the summary
        await store.UpdateSummaryAsync(containerGuid, null, null, null);

        // Assert
        Container? reloaded = await store.GetAsync(containerGuid);
        reloaded.Should().NotBeNull();
        reloaded!.Summary.Should().BeNull();
        reloaded.SummaryGeneratedAt.Should().BeNull();

        // Cleanup
        await fixture.AdminClient.DeleteAsync($"/api/containers/{created.Id}");
    }

    [Fact]
    public async Task UpdateSummaryAsync_NonExistentContainer_DoesNotThrow()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IContainerStore>();

        // Should silently return, not throw
        var act = async () => await store.UpdateSummaryAsync(Guid.NewGuid(), "summary", DateTime.UtcNow, null);
        await act.Should().NotThrowAsync();
    }

    private record ContainerDto(string Id, string Name);
}
