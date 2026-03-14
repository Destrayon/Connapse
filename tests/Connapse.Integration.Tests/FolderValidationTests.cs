using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class FolderValidationTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task CreateFolder_PathTooDeep_Returns400()
    {
        var container = await CreateTestContainer();
        try
        {
            var deepPath = "/" + string.Join("/", Enumerable.Range(0, 51).Select(i => $"d{i}"));
            var response = await fixture.AdminClient.PostAsJsonAsync(
                $"/api/containers/{container.Id}/folders",
                new { Path = deepPath });
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            body.GetProperty("error").GetString().Should().Be("path_too_deep");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
        }
    }

    [Fact]
    public async Task CreateFolder_PathAtMaxDepth_Returns201()
    {
        var container = await CreateTestContainer();
        try
        {
            var maxPath = "/" + string.Join("/", Enumerable.Range(0, 50).Select(i => $"d{i}"));
            var response = await fixture.AdminClient.PostAsJsonAsync(
                $"/api/containers/{container.Id}/folders",
                new { Path = maxPath });
            response.StatusCode.Should().Be(HttpStatusCode.Created);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
        }
    }

    private async Task<ContainerDto> CreateTestContainer()
    {
        var name = $"folder-val-{Guid.NewGuid():N}"[..20];
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = name });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions))!;
    }

    private record ContainerDto(string Id, string Name);
}
