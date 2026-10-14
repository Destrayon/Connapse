using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Connapse.CustomSource.Example.Tests;

[Trait("Category", "Unit")]
public class ChangeFeedTests
{
    private static ExampleServer NewServer(int pageSize = 2) => new(ExampleServer.DefaultConfig() with { PageSize = pageSize }, new Dictionary<string, string>
    {
        ["eng/README.md"] = "# Engineering",
        ["eng/design/secret.md"] = "# Secret",
    });

    [Fact]
    public async Task Changes_AfterListing_ReportAddChangeAndDelete()
    {
        using ExampleServer server = NewServer(pageSize: 10);
        string cursor = await ListToEnd(server, "eng");

        server.WriteFile("eng/new.md", "# New");
        server.WriteFile("eng/README.md", "# Engineering, edited");
        server.DeleteFile("eng/design/secret.md");

        JsonElement page = await DiscoveryTests.GetJson(server, $"/v1/scopes/eng/changes?cursor={Uri.EscapeDataString(cursor)}");

        Dictionary<string, string> ops = page.GetProperty("items").EnumerateArray().ToDictionary(
            i => i.TryGetProperty("path", out JsonElement p) ? p.GetString()! : "deleted",
            i => i.GetProperty("op").GetString()!);
        ops.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["new.md"] = "upsert",
            ["README.md"] = "upsert",
            ["deleted"] = "delete",
        });
        string next = page.GetProperty("next_cursor").GetString()!;

        JsonElement quiet = await DiscoveryTests.GetJson(server, $"/v1/scopes/eng/changes?cursor={Uri.EscapeDataString(next)}");
        quiet.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Changes_MoreThanOnePage_UsePageTokenThenNextCursor()
    {
        using ExampleServer server = NewServer();
        string cursor = await ListToEnd(server, "eng");
        server.WriteFile("eng/a.md", "a");
        server.WriteFile("eng/b.md", "b");
        server.WriteFile("eng/c.md", "c");

        JsonElement first = await DiscoveryTests.GetJson(server, $"/v1/scopes/eng/changes?cursor={Uri.EscapeDataString(cursor)}");
        first.GetProperty("items").GetArrayLength().Should().Be(2);
        first.TryGetProperty("next_cursor", out _).Should().BeFalse();
        string token = first.GetProperty("next_page_token").GetString()!;

        JsonElement second = await DiscoveryTests.GetJson(server, $"/v1/scopes/eng/changes?page_token={Uri.EscapeDataString(token)}");
        second.GetProperty("items").GetArrayLength().Should().Be(1);
        second.GetProperty("next_cursor").GetString().Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("deadbeef.0")]
    [InlineData("garbage")]
    public async Task Changes_CursorNotIssuedByThisServer_Returns410(string cursor)
    {
        using ExampleServer server = NewServer();
        await ListToEnd(server, "eng");

        HttpResponseMessage response = await server.Client().GetAsync($"/v1/scopes/eng/changes?cursor={cursor}");

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Changes_CursorBeyondTheHead_Returns410()
    {
        using ExampleServer server = NewServer();
        string cursor = await ListToEnd(server, "eng");
        string epoch = cursor[..cursor.IndexOf('.')];

        HttpResponseMessage response = await server.Client().GetAsync($"/v1/scopes/eng/changes?cursor={epoch}.999");

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Changes_WithoutCursor_Returns400()
    {
        using ExampleServer server = NewServer();

        HttpResponseMessage response = await server.Client().GetAsync("/v1/scopes/eng/changes");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<string> ListToEnd(ExampleServer server, string scope)
    {
        string url = $"/v1/scopes/{scope}/documents";
        while (true)
        {
            JsonElement page = await DiscoveryTests.GetJson(server, url);
            if (page.TryGetProperty("change_cursor", out JsonElement cursor))
                return cursor.GetString()!;
            url = $"/v1/scopes/{scope}/documents?page_token={Uri.EscapeDataString(page.GetProperty("next_page_token").GetString()!)}";
        }
    }
}
