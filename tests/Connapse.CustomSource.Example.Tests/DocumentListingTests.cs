using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace Connapse.CustomSource.Example.Tests;

[Trait("Category", "Unit")]
public class DocumentListingTests
{
    private static readonly Dictionary<string, string> Files = new()
    {
        ["eng/README.md"] = "# Engineering",
        ["eng/design/architecture.md"] = "# Architecture",
        ["eng/design/secret.md"] = "# Secret",
        ["ops/runbook.md"] = "# Runbook",
    };

    [Fact]
    public async Task Listing_PagesThroughEveryDocument_AndEndsWithChangeCursor()
    {
        using ExampleServer server = new(files: Files);

        JsonElement first = await DiscoveryTests.GetJson(server, "/v1/scopes/eng/documents");
        first.GetProperty("items").GetArrayLength().Should().Be(2);
        first.TryGetProperty("change_cursor", out _).Should().BeFalse();
        string token = first.GetProperty("next_page_token").GetString()!;

        JsonElement second = await DiscoveryTests.GetJson(server, $"/v1/scopes/eng/documents?page_token={Uri.EscapeDataString(token)}");
        second.GetProperty("items").GetArrayLength().Should().Be(1);
        second.TryGetProperty("next_page_token", out _).Should().BeFalse();
        second.GetProperty("change_cursor").GetString().Should().NotBeNullOrEmpty();

        first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray())
            .Select(i => i.GetProperty("path").GetString())
            .Should().BeEquivalentTo("README.md", "design/architecture.md", "design/secret.md");
    }

    [Fact]
    public async Task Listing_AttachesOneAccessKeyPerTopLevelFolder()
    {
        using ExampleServer server = new(ExampleServer.DefaultConfig() with { PageSize = 10 }, Files);

        JsonElement root = await DiscoveryTests.GetJson(server, "/v1/scopes/eng/documents");

        Dictionary<string, string> keys = root.GetProperty("items").EnumerateArray().ToDictionary(
            i => i.GetProperty("path").GetString()!,
            i => i.GetProperty("access_keys")[0].GetString()!);
        keys["README.md"].Should().Be("folder:eng/");
        keys["design/architecture.md"].Should().Be("folder:eng/design");
        root.GetProperty("items")[0].GetProperty("access_version").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Listing_ContentOnlyConfig_OmitsAccessKeys()
    {
        using ExampleServer server = new(ExampleServer.DefaultConfig() with { PermissionsEnabled = false }, Files);

        JsonElement root = await DiscoveryTests.GetJson(server, "/v1/scopes/eng/documents");

        root.GetProperty("items")[0].TryGetProperty("access_keys", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Listing_UnknownScope_Returns404()
    {
        using ExampleServer server = new(files: Files);

        HttpResponseMessage response = await server.Client().GetAsync("/v1/scopes/nope/documents");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Listing_MalformedPageToken_Returns400()
    {
        using ExampleServer server = new(files: Files);

        HttpResponseMessage response = await server.Client().GetAsync("/v1/scopes/eng/documents?page_token=%%%");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Content_ReturnsBytesWithETag_And304WhenUnchanged()
    {
        using ExampleServer server = new(files: Files);
        HttpClient client = server.Client();
        string id = Base64Url.EncodeToString(Encoding.UTF8.GetBytes("README.md"));

        HttpResponseMessage response = await client.GetAsync($"/v1/scopes/eng/documents/{id}/content");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("# Engineering");
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");
        string etag = response.Headers.ETag!.Tag;

        HttpRequestMessage again = new(HttpMethod.Get, $"/v1/scopes/eng/documents/{id}/content");
        again.Headers.TryAddWithoutValidation("If-None-Match", etag);
        (await client.SendAsync(again)).StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [Theory]
    [InlineData("../ops/runbook.md")]
    [InlineData("/etc/passwd")]
    [InlineData("design/../../ops/runbook.md")]
    public async Task Content_PathOutsideScope_Returns404(string relativePath)
    {
        using ExampleServer server = new(files: Files);
        string id = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(relativePath));

        HttpResponseMessage response = await server.Client().GetAsync($"/v1/scopes/eng/documents/{id}/content");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Content_IdThatIsNotBase64Url_Returns404()
    {
        using ExampleServer server = new(files: Files);

        HttpResponseMessage response = await server.Client().GetAsync("/v1/scopes/eng/documents/not*base64/content");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
