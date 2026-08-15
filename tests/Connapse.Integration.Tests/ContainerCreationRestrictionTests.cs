using System.Net;
using System.Net.Http.Json;
using Connapse.Core;
using FluentAssertions;
using Xunit;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class ContainerCreationRestrictionTests(SharedWebAppFixture fixture)
{
    private static string ShortName(string p) => $"{p}-{Guid.NewGuid():N}"[..20];

    [Theory]
    [InlineData(ConnectorType.S3)]
    [InlineData(ConnectorType.AzureBlob)]
    [InlineData(ConnectorType.Filesystem)]
    public async Task CreateContainer_NonManagedConnectorType_Returns400(ConnectorType type)
    {
        // External storage is a source now. Allowing a container of this type would
        // recreate the unwritable-container case that ContainerWriteGuard existed for.
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = ShortName("ext"), ConnectorType = type });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("source");
    }

    [Theory]
    [InlineData(ConnectorType.S3, """{"bucketName":"b","region":"us-east-1"}""")]
    [InlineData(ConnectorType.AzureBlob, """{"storageAccountName":"a","containerName":"c"}""")]
    [InlineData(ConnectorType.Filesystem, """{"rootPath":"/data"}""")]
    public async Task CreateContainer_FullyConfiguredExternalConnector_IsStillRejected(
        ConnectorType type, string connectorConfig)
    {
        // This is the actual hole. A request missing its connector config already 400s on
        // validation, which would mask a missing restriction — a *valid* external config
        // is what would otherwise create a writable non-managed container.
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = ShortName("ext-cfg"), ConnectorType = type, ConnectorConfig = connectorConfig });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("source");
    }

    [Fact]
    public async Task CreateContainer_ManagedStorage_StillSucceeds()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = ShortName("managed") });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateContainer_ExplicitManagedStorage_StillSucceeds()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = ShortName("managed2"), ConnectorType = ConnectorType.ManagedStorage });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
