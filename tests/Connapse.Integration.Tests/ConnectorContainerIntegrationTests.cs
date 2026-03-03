using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for connector-type-specific container creation.
/// Validates endpoint validation logic for each ConnectorType.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class ConnectorContainerIntegrationTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task CreateContainer_MinIODefault_Returns201()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "connector-minio-test", ConnectorType = ConnectorType.MinIO });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var container = await response.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);
        container.Should().NotBeNull();
        container!.Name.Should().Be("connector-minio-test");

        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    [Fact]
    public async Task CreateContainer_InMemory_Returns201()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "connector-inmemory-test", ConnectorType = ConnectorType.InMemory });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var container = await response.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);
        container.Should().NotBeNull();

        await fixture.AdminClient.DeleteAsync($"/api/containers/{container!.Id}");
    }

    [Fact]
    public async Task CreateContainer_Filesystem_MissingConfig_Returns400()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "connector-fs-noconfig", ConnectorType = ConnectorType.Filesystem });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions);
        body!.Error.Should().Contain("rootPath");
    }

    [Fact]
    public async Task CreateContainer_Filesystem_ValidConfig_Returns201()
    {
        var config = JsonSerializer.Serialize(new { rootPath = "C:\\temp\\connapse-test-" + Guid.NewGuid() });
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "connector-fs-valid", ConnectorType = ConnectorType.Filesystem, ConnectorConfig = config });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var container = await response.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);
        container.Should().NotBeNull();

        await fixture.AdminClient.DeleteAsync($"/api/containers/{container!.Id}");
    }

    [Fact]
    public async Task CreateContainer_S3_MissingConfig_Returns400()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "connector-s3-noconfig", ConnectorType = ConnectorType.S3 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions);
        body!.Error.Should().Contain("bucketName");
    }

    [Fact]
    public async Task CreateContainer_S3_ValidConfig_Returns201()
    {
        var config = JsonSerializer.Serialize(new { bucketName = "test-bucket", region = "us-east-1" });
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "connector-s3-valid", ConnectorType = ConnectorType.S3, ConnectorConfig = config });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var container = await response.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);
        container.Should().NotBeNull();

        await fixture.AdminClient.DeleteAsync($"/api/containers/{container!.Id}");
    }

    [Fact]
    public async Task CreateContainer_AzureBlob_MissingConfig_Returns400()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "connector-azure-noconfig", ConnectorType = ConnectorType.AzureBlob });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions);
        body!.Error.Should().Contain("storageAccountName");
    }

    [Fact]
    public async Task CreateContainer_AzureBlob_ValidConfig_Returns201()
    {
        var config = JsonSerializer.Serialize(new { storageAccountName = "testaccount", containerName = "docs" });
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "connector-azure-valid", ConnectorType = ConnectorType.AzureBlob, ConnectorConfig = config });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var container = await response.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);
        container.Should().NotBeNull();

        await fixture.AdminClient.DeleteAsync($"/api/containers/{container!.Id}");
    }

    // DTOs
    private record ContainerDto(string Id, string Name, string? Description);
    private record ErrorResponse(string Error);
}
