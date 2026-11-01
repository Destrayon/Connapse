using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;

namespace Connapse.CustomSource.Example.Tests;

[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class DiscoveryTests
{
    [Fact]
    public async Task Discovery_WithoutToken_Returns401Problem()
    {
        using ExampleServer server = new();

        HttpResponseMessage response = await server.Client(authorized: false).GetAsync("/.well-known/connapse-source");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Discovery_WithWrongToken_Returns401()
    {
        using ExampleServer server = new();
        HttpClient client = server.Client(authorized: false);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");

        HttpResponseMessage response = await client.GetAsync("/.well-known/connapse-source");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Discovery_FullConfig_AdvertisesEveryCapability()
    {
        using ExampleServer server = new();

        JsonElement root = await GetJson(server, "/.well-known/connapse-source");

        root.GetProperty("protocol_versions")[0].GetString().Should().Be("1");
        root.GetProperty("name").GetString().Should().Be("Test bridge");
        JsonElement capabilities = root.GetProperty("capabilities");
        capabilities.GetProperty("changes").GetBoolean().Should().BeTrue();
        capabilities.GetProperty("tombstones").GetBoolean().Should().BeTrue();
        capabilities.GetProperty("max_page_size").GetInt32().Should().Be(2);
        JsonElement permissions = capabilities.GetProperty("permissions");
        permissions.GetProperty("check").GetProperty("max_batch").GetInt32().Should().Be(100);
        permissions.GetProperty("access_keys").GetProperty("cache_ttl_seconds").GetInt32().Should().Be(0);
        permissions.GetProperty("linking").GetProperty("link_url").GetString().Should().EndWith("/link");
    }

    [Fact]
    public async Task Discovery_ContentOnlyConfig_OmitsPermissions()
    {
        using ExampleServer server = new(ExampleServer.DefaultConfig() with { PermissionsEnabled = false, LinkSecret = null });

        JsonElement root = await GetJson(server, "/.well-known/connapse-source");

        root.GetProperty("capabilities").TryGetProperty("permissions", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Scopes_ListsConfiguredScopes()
    {
        using ExampleServer server = new();

        JsonElement root = await GetJson(server, "/v1/scopes");

        root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString())
            .Should().Equal("eng", "ops");
        root.TryGetProperty("next_page_token", out _).Should().BeFalse();
    }

    internal static async Task<JsonElement> GetJson(ExampleServer server, string url)
    {
        HttpResponseMessage response = await server.Client().GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
