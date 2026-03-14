using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SearchValidationTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // ── topK validation ─────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task Search_InvalidTopK_Returns400(int topK)
    {
        var container = await CreateTestContainer();
        try
        {
            var response = await fixture.AdminClient.GetAsync(
                $"/api/containers/{container.Id}/search?q=test&topK={topK}");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            body.GetProperty("error").GetString().Should().Be("topk_out_of_range");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task Search_BoundaryTopK_Returns200(int topK)
    {
        var container = await CreateTestContainer();
        try
        {
            var response = await fixture.AdminClient.GetAsync(
                $"/api/containers/{container.Id}/search?q=test&topK={topK}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
        }
    }

    // ── minScore validation ─────────────────────────────────────────

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public async Task Search_InvalidMinScore_Returns400(double minScore)
    {
        var container = await CreateTestContainer();
        try
        {
            var response = await fixture.AdminClient.GetAsync(
                $"/api/containers/{container.Id}/search?q=test&minScore={minScore}");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            body.GetProperty("error").GetString().Should().Be("minscore_out_of_range");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public async Task Search_BoundaryMinScore_Returns200(double minScore)
    {
        var container = await CreateTestContainer();
        try
        {
            var response = await fixture.AdminClient.GetAsync(
                $"/api/containers/{container.Id}/search?q=test&minScore={minScore}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
        }
    }

    // ── query length validation ─────────────────────────────────────

    [Fact]
    public async Task Search_QueryTooLong_Returns400()
    {
        var container = await CreateTestContainer();
        try
        {
            var longQuery = new string('a', 10_001);
            var response = await fixture.AdminClient.GetAsync(
                $"/api/containers/{container.Id}/search?q={longQuery}");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            body.GetProperty("error").GetString().Should().Be("query_too_long");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
        }
    }

    [Fact]
    public async Task Search_QueryAtMaxLength_Returns200()
    {
        var container = await CreateTestContainer();
        try
        {
            var maxQuery = new string('a', 10_000);
            var response = await fixture.AdminClient.GetAsync(
                $"/api/containers/{container.Id}/search?q={maxQuery}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
        }
    }

    // ── POST endpoint validation ────────────────────────────────────

    [Fact]
    public async Task SearchPost_InvalidTopK_Returns400()
    {
        var container = await CreateTestContainer();
        try
        {
            var response = await fixture.AdminClient.PostAsJsonAsync(
                $"/api/containers/{container.Id}/search",
                new { Query = "test", TopK = 0 });
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            body.GetProperty("error").GetString().Should().Be("topk_out_of_range");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private async Task<ContainerDto> CreateTestContainer()
    {
        var name = $"search-val-{Guid.NewGuid():N}"[..20];
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = name });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions))!;
    }

    private record ContainerDto(string Id, string Name);
}
