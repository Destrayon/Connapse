using System.Net;
using System.Net.Http.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Endpoints;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// The boundary epic #348 exists to establish: a container is browsable, a source is not.
/// <para>
/// Every assertion here is paired. Checking only that a source is refused would pass on a
/// server that had broken browsing entirely, and checking only that a container works would
/// pass on one that browsed everything. The property is the *difference* between them, so both
/// halves are asserted against the same routes in the same test.
/// </para>
/// <para>
/// Refusal, not emptiness. An empty listing satisfies "no files were shown" today and silently
/// becomes a real listing the first time someone widens a lookup — which is exactly the
/// regression this file exists to catch.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class FileBrowserBoundaryTests(SharedWebAppFixture fixture)
{
    private static string ShortName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private HttpClient Admin => fixture.AdminClient;

    private async Task<Guid> SeedContainerAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var containers = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        var container = await containers.CreateAsync(
            new CreateContainerRequest(ShortName("cnt"), null, ConnectorType.ManagedStorage, null));

        return Guid.Parse(container.Id);
    }

    private async Task<Guid> SeedSourceAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();

        var connection = await connections.CreateAsync(
            new CreateConnectionRequest(ShortName("conn"), ConnectionProvider.S3, """{"region":"us-east-1"}"""),
            createdByUserId: null);

        var source = await sources.CreateAsync(
            new CreateSourceRequest(ShortName("src"), connection.Id, """{"bucketName":"b"}"""));

        return source.Id;
    }

    [Fact]
    public async Task BrowseRoute_ManagedContainerAndSourceIds_ReturnsOkAndNotFound()
    {
        Guid containerId = await SeedContainerAsync();
        Guid sourceId = await SeedSourceAsync();

        var container = await Admin.GetAsync($"/api/containers/{containerId}/files?path=/");
        var source = await Admin.GetAsync($"/api/containers/{sourceId}/files?path=/");

        container.StatusCode.Should().Be(HttpStatusCode.OK,
            "browsing managed storage is the whole point of the file browser");

        source.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a source has no file listing — refusing it is the boundary this epic establishes");
    }

    [Fact]
    public async Task CreateFolder_SourceId_IsRefused()
    {
        Guid sourceId = await SeedSourceAsync();

        var response = await Admin.PostAsJsonAsync(
            $"/api/containers/{sourceId}/folders", new { path = "/new-folder" });

        // Writing into a source would mutate somebody else's system through Connapse.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ContainerStats_ManagedContainerAndSourceIds_ReturnsOkAndNotFound()
    {
        Guid containerId = await SeedContainerAsync();
        Guid sourceId = await SeedSourceAsync();

        (await Admin.GetAsync($"/api/containers/{containerId}/stats")).StatusCode
            .Should().Be(HttpStatusCode.OK);

        (await Admin.GetAsync($"/api/containers/{sourceId}/stats")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task BrowseRoute_LegacyNonManagedContainer_IsRefused()
    {
        // The un-migrated window is real: the #350 backfill may fail without blocking boot, and
        // skips entirely when another replica holds its advisory lock — so a row can still carry
        // a Filesystem connector type. Seeded through the store rather than the API, because the
        // API rejects this shape and that is precisely the state being simulated.
        Guid legacyId;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var containers = scope.ServiceProvider.GetRequiredService<IContainerStore>();
            var legacy = await containers.CreateAsync(new CreateContainerRequest(
                ShortName("legacy"), null, ConnectorType.Filesystem, """{"rootPath":"/tmp"}"""));
            legacyId = Guid.Parse(legacy.Id);
        }

        var browse = await Admin.GetAsync($"/api/containers/{legacyId}/files?path=/");
        var write = await Admin.PostAsJsonAsync(
            $"/api/containers/{legacyId}/folders", new { path = "/new-folder" });

        browse.StatusCode.Should().NotBe(HttpStatusCode.OK,
            "an unmigrated external container must not be browsable through the container routes");
        write.StatusCode.Should().NotBe(HttpStatusCode.OK,
            "and it must certainly not be writable — that would mutate someone else's system");
    }

    [Fact]
    public async Task CreateContainer_NonManagedConnectorType_IsRejected()
    {
        // The create form no longer offers these, but the form is not the boundary — this is.
        // A client posting directly must still be refused.
        foreach (var connectorType in new[] { ConnectorType.Filesystem, ConnectorType.S3, ConnectorType.AzureBlob })
        {
            // The shared request record rather than an anonymous object: if the API contract
            // changes shape, this fails to compile instead of silently posting the wrong body
            // and still getting the 400 it expects.
            var response = await Admin.PostAsJsonAsync("/api/containers", new CreateContainerApiRequest(
                Name: ShortName("bad"),
                ConnectorType: connectorType,
                ConnectorConfig: """{"rootPath":"/tmp"}"""));

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                $"{connectorType} is a source, not a container");
        }
    }
}
