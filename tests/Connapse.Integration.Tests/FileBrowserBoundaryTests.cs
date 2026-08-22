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
/// <para>
/// The legacy-container cases these tests used to carry are gone with #353: a container row can
/// no longer record an external connector, so "an unmigrated external container" is not a state
/// the schema can express. A source id posted at a container route still is, and that is the
/// case every test below exercises.
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
        var container = await containers.CreateAsync(new CreateContainerRequest(ShortName("cnt")));

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
            $"/api/containers/{sourceId}/folders", new CreateFolderRequest("/new-folder"));

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
    public async Task GetContainer_ManagedContainerAndSourceIds_ReturnsOkAndNotFound()
    {
        // #350's compatibility read answered this with the source projected into the container
        // shape. #353 removed it, and this pins that: a source is served by /api/sources/{id},
        // and asking the container route for one now gets nothing.
        Guid containerId = await SeedContainerAsync();
        Guid sourceId = await SeedSourceAsync();

        (await Admin.GetAsync($"/api/containers/{containerId}")).StatusCode
            .Should().Be(HttpStatusCode.OK);

        (await Admin.GetAsync($"/api/containers/{sourceId}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound,
                "the compatibility read is gone; a source resolves only at /api/sources");
    }

    [Fact]
    public async Task DirectFileRoutes_SourceId_AreRefused()
    {
        // The listing route was the obvious hole and got fixed first. These are the ones that
        // survive closing it: reading a document by id returns its metadata, and /content
        // returns the bytes themselves out of the external system. Closing the listing while
        // leaving these open would look like the boundary held.
        Guid sourceId = await SeedSourceAsync();
        string fileId = Guid.NewGuid().ToString();

        // The exact status, not merely "not OK" — that would also pass on a 500, which is a
        // broken server rather than an enforced boundary.
        (await Admin.GetAsync($"/api/containers/{sourceId}/files/{fileId}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "document metadata must not be readable");

        (await Admin.GetAsync($"/api/containers/{sourceId}/files/{fileId}/content")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "and its bytes certainly must not be");
    }
}
