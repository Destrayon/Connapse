using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class PaginationValidationTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // ── Container list endpoint ────────────────────────────────────────

    [Theory]
    [InlineData(-1, 50, "skip must be >= 0")]
    [InlineData(0, 0, "take must be >= 1")]
    [InlineData(0, -5, "take must be >= 1")]
    [InlineData(0, 201, "take must be <= 200")]
    [InlineData(0, 999, "take must be <= 200")]
    public async Task ListContainers_InvalidPagination_Returns400(int skip, int take, string expectedError)
    {
        var response = await fixture.AdminClient.GetAsync($"/api/containers?skip={skip}&take={take}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions);
        body!.Error.Should().Be(expectedError);
    }

    [Fact]
    public async Task ListContainers_ValidPagination_Returns200()
    {
        var response = await fixture.AdminClient.GetAsync("/api/containers?skip=0&take=50");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ListContainers_BoundaryValues_Returns200()
    {
        var response = await fixture.AdminClient.GetAsync("/api/containers?skip=0&take=1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        response = await fixture.AdminClient.GetAsync("/api/containers?skip=0&take=200");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Users list endpoint ────────────────────────────────────────────

    [Theory]
    [InlineData(-1, 50, "skip must be >= 0")]
    [InlineData(0, 0, "take must be >= 1")]
    [InlineData(0, 201, "take must be <= 200")]
    public async Task ListUsers_InvalidPagination_Returns400(int skip, int take, string expectedError)
    {
        var response = await fixture.AdminClient.GetAsync($"/api/v1/auth/users?skip={skip}&take={take}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions);
        body!.Error.Should().Be(expectedError);
    }

    // ── File list endpoint ─────────────────────────────────────────────

    [Theory]
    [InlineData(-1, 50, "skip must be >= 0")]
    [InlineData(0, 0, "take must be >= 1")]
    [InlineData(0, 201, "take must be <= 200")]
    public async Task ListFiles_InvalidPagination_Returns400(int skip, int take, string expectedError)
    {
        // Create a container to test file listing
        var createResponse = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "pagination-test-files" });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var container = await createResponse.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);

        try
        {
            var response = await fixture.AdminClient.GetAsync(
                $"/api/containers/{container!.Id}/files?skip={skip}&take={take}");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var body = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions);
            body!.Error.Should().Be(expectedError);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container!.Id}");
        }
    }

    private record ErrorResponse(string Error);
    private record ContainerDto(string Id, string Name, string? Description, int DocumentCount);
}
