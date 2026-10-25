using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>POST /api/sources/{sourceId}/search (#916): a source searched over REST as a container is.</summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SourceSearchEndpointTests(SharedWebAppFixture fixture)
{
    private static string ShortName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private async Task<Guid> SeedSourceAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var connection = await scope.ServiceProvider.GetRequiredService<IConnectionStore>().CreateAsync(
            new CreateConnectionRequest(ShortName("c"), ConnectionProvider.S3, """{"region":"us-east-1"}"""), createdByUserId: null);
        var source = await scope.ServiceProvider.GetRequiredService<ISourceStore>().CreateAsync(
            new CreateSourceRequest(ShortName("s"), connection.Id, """{"bucketName":"b"}"""));
        return source.Id;
    }

    [Fact]
    public async Task SearchSource_DocumentInTheSource_IsFound()
    {
        var sourceId = await SeedSourceAsync();
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            using var content = new MemoryStream("the heliotrope ledger lists every lighthouse keeper"u8.ToArray());
            await scope.ServiceProvider.GetRequiredService<IKnowledgeIngester>().IngestAsync(content,
                new IngestionOptions(FileName: "ledger.md", ContentType: "text/markdown", Path: "/ledger.md") { Owner = OwnerRef.ForSource(sourceId) },
                CancellationToken.None);
        }

        var response = await fixture.AdminClient.PostAsJsonAsync($"/api/sources/{sourceId}/search", new { query = "heliotrope ledger", mode = "Keyword" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("content").GetString())
            .Should().Contain(c => c!.Contains("heliotrope", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchSource_UnknownSource_Returns404()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync($"/api/sources/{Guid.NewGuid()}/search", new { query = "anything" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString().Should().EndWith("#search-scope-not-found");
    }

    [Fact]
    public async Task SearchSource_ContainerId_Returns404()
    {
        var created = await fixture.AdminClient.PostAsJsonAsync("/api/containers", new { Name = ShortName("search-src") });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        string containerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        try
        {
            var response = await fixture.AdminClient.PostAsJsonAsync($"/api/sources/{containerId}/search", new { query = "anything" });

            response.StatusCode.Should().Be(HttpStatusCode.NotFound, "the source route searches sources only");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task SearchSource_EmptyQuery_Returns400BeforeLookingTheSourceUp()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync($"/api/sources/{Guid.NewGuid()}/search", new { query = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("type").GetString().Should().EndWith("#invalid-search");
        body.GetProperty("field").GetString().Should().Be("query");
    }

    [Fact]
    public async Task SearchSource_Unauthenticated_Returns401()
    {
        using var anonymous = fixture.Factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync($"/api/sources/{Guid.NewGuid()}/search", new { query = "anything" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
