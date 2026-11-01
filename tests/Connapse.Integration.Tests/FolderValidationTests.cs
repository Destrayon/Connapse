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
            body.GetProperty("type").GetString().Should().EndWith("#invalid-folder");
            body.GetProperty("field").GetString().Should().Be("path");
            body.GetProperty("error").GetString().Should().Be("path_too_deep", "scripts branch on the code");
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

    // The fuzz run's folder (CON-179): it was created, then the response failed with a 500 because a response
    // header can't hold a non-ASCII or control character.
    [Theory]
    [InlineData("/Café")]
    [InlineData("/\u0098ß\u0081Ñ\u008aeøp")]
    public async Task CreateFolder_NonAsciiPath_Returns201WithAnEncodedLocation(string path)
    {
        var container = await CreateTestContainer();
        try
        {
            var response = await fixture.AdminClient.PostAsJsonAsync(
                $"/api/containers/{container.Id}/folders",
                new { Path = path });

            response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            var folder = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            response.Headers.Location!.OriginalString.Should().Be(
                $"/api/containers/{container.Id}/folders?path={Uri.EscapeDataString(folder.GetProperty("path").GetString()!)}");
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
