using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using FluentAssertions;
using ModelContextProtocol.Client;

namespace Connapse.Integration.Tests.CrossSurface;

/// <summary>
/// An MCP upload keeps the folder path it is given (CON-358). An agent that uploads docs/guides/a.md must find it in
/// /docs/guides/, with /docs/ and /docs/guides/ made on the way, not flattened into the root where same-named files
/// from different folders would collide.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class McpUploadFolderPathTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Theory]
    [InlineData("docs/guides/")]
    [InlineData("/docs/guides/")]
    [InlineData("docs/guides")]
    public async Task UploadFile_WithNestedPath_LandsInThatFolder(string path)
    {
        string containerId = await CreateContainerAsync();
        await using var mcp = await Surfaces.McpAsync(fixture);

        string text = Text(await mcp.CallToolAsync("upload_file", new Dictionary<string, object?>
        {
            ["containerId"] = containerId, ["fileName"] = "a.md", ["textContent"] = "# A", ["path"] = path,
        }));

        text.Should().Contain("uploaded to /docs/guides/a.md");
        await AssertNestedLayoutAsync(containerId, "/docs/guides/", ["/docs/guides/a.md"]);
    }

    [Fact]
    public async Task UploadFile_FilesWithNestedFolderPaths_KeepsEachFileInItsFolder()
    {
        string containerId = await CreateContainerAsync();
        await using var mcp = await Surfaces.McpAsync(fixture);

        string result = Surfaces.Result(await mcp.CallToolAsync("upload_file", new Dictionary<string, object?>
        {
            ["containerId"] = containerId,
            ["files"] = """
                [{"filename":"a.md","content":"# guides","folderPath":"docs/guides/"},
                 {"filename":"a.md","content":"# api","folderPath":"docs/api/"}]
                """,
        }));

        result.Should().Be("ok", "two files with the same name in different folders don't collide");
        await AssertNestedLayoutAsync(containerId, "/docs/guides/", ["/docs/guides/a.md"]);
        (await ListPathsAsync(containerId, "/docs/api/")).Should().Equal("/docs/api/a.md");
    }

    [Fact]
    public async Task UploadFile_PathInFileName_IsRefusedRatherThanFlattened()
    {
        string containerId = await CreateContainerAsync();
        await using var mcp = await Surfaces.McpAsync(fixture);

        string result = Surfaces.Result(await mcp.CallToolAsync("upload_file", new Dictionary<string, object?>
        {
            ["containerId"] = containerId, ["fileName"] = "docs/guides/a.md", ["textContent"] = "# A",
        }));

        result.Should().Be("error:validation");
        (await ListPathsAsync(containerId, "/")).Should().BeEmpty("nothing lands in the root under the bare file name");
    }

    private async Task AssertNestedLayoutAsync(string containerId, string folder, string[] files)
    {
        (await ListPathsAsync(containerId, "/")).Should().Equal("/docs/");
        (await ListPathsAsync(containerId, "/docs/")).Should().Contain("/docs/guides/");
        (await ListPathsAsync(containerId, folder)).Should().Equal(files);
    }

    private async Task<string> CreateContainerAsync()
    {
        var created = await fixture.AdminClient.PostAsJsonAsync("/api/containers", new { name = $"mcp-path-{Guid.NewGuid():N}"[..24] });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions))!.Id;
    }

    private async Task<List<string>> ListPathsAsync(string containerId, string path)
    {
        var response = await fixture.AdminClient.GetAsync($"/api/containers/{containerId}/files?path={Uri.EscapeDataString(path)}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<PagedResponse<BrowseEntryDto>>(JsonOptions);
        return [.. page!.Items.Select(e => e.Path)];
    }

    private static string Text(ModelContextProtocol.Protocol.CallToolResult result)
    {
        result.IsError.Should().NotBe(true);
        return string.Join("\n", result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text));
    }

    private sealed record ContainerDto(string Id);
    private sealed record BrowseEntryDto(string Name, string Path, bool IsFolder);
}
