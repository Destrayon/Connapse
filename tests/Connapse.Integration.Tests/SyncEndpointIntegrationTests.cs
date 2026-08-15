using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for POST /api/containers/{id}/sync endpoint.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SyncEndpointIntegrationTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Sync_FilesystemContainer_Returns400 was removed in #351: Filesystem containers
    // can no longer be created, so the scenario is unreachable. Container creation is
    // restricted to managed storage by ContainerCreationRestrictionTests.

    [Fact]
    public async Task Sync_NonExistentContainer_Returns404()
    {
        var fakeId = Guid.NewGuid();
        var syncResponse = await fixture.AdminClient.PostAsync(
            $"/api/containers/{fakeId}/sync", null);

        syncResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sync_MinIOContainer_ReturnsOk()
    {
        // MinIO container with real Testcontainers MinIO — sync should succeed (empty bucket)
        var createResponse = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "sync-minio-test", ConnectorType = ConnectorType.ManagedStorage });
        var container = await createResponse.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);

        var syncResponse = await fixture.AdminClient.PostAsync(
            $"/api/containers/{container!.Id}/sync", null);

        syncResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await syncResponse.Content.ReadFromJsonAsync<SyncResult>(JsonOptions);
        result.Should().NotBeNull();
        result!.EnqueuedCount.Should().Be(0, "empty container has nothing to sync");
        result.SkippedCount.Should().Be(0);

        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    // DTOs
    private record ContainerDto(string Id, string Name);
    private record ErrorResponse(string Error);
    private record SyncResult(string BatchId, int TotalFiles, int EnqueuedCount, int SkippedCount);
}
