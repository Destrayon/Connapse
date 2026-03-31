using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using System.Net.Http.Headers;

namespace Connapse.Integration.Tests;

[Collection("Integration Tests")]
[Trait("Category", "Integration")]
public class OAuthEndpointTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task ProtectedResourceMetadata_ReturnsValidJson()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/.well-known/oauth-protected-resource");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonDocument>();
        json!.RootElement.GetProperty("resource").GetString().Should().NotBeNullOrWhiteSpace();
        json.RootElement.GetProperty("authorization_servers").GetArrayLength().Should().BeGreaterThan(0);
        json.RootElement.GetProperty("scopes_supported").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task AuthorizationServerMetadata_ReturnsValidJson()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/.well-known/oauth-authorization-server");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonDocument>();
        var root = json!.RootElement;
        root.GetProperty("issuer").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("authorization_endpoint").GetString().Should().Contain("/oauth/authorize");
        root.GetProperty("token_endpoint").GetString().Should().Contain("/oauth/token");
        root.GetProperty("registration_endpoint").GetString().Should().Contain("/oauth/register");
        root.GetProperty("code_challenge_methods_supported").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("S256");
        root.GetProperty("client_id_metadata_document_supported").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Register_ValidRequest_Returns201WithClientId()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = "Test Client",
            redirect_uris = new[] { "http://127.0.0.1:3000/callback" },
            application_type = "native",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await response.Content.ReadFromJsonAsync<JsonDocument>();
        json!.RootElement.GetProperty("client_id").GetString().Should().NotBeNullOrWhiteSpace();
        json.RootElement.GetProperty("client_name").GetString().Should().Be("Test Client");
    }

    [Fact]
    public async Task Register_InvalidRedirectUri_Returns400()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = "Bad Client",
            redirect_uris = new[] { "ftp://evil.com" },
            application_type = "native",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TokenEndpoint_InvalidGrant_Returns400()
    {
        using var client = fixture.Factory.CreateClient();

        var formContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = "invalid-code",
            ["redirect_uri"] = "http://127.0.0.1:3000/callback",
            ["client_id"] = "test",
            ["code_verifier"] = "test-verifier",
        });

        var response = await client.PostAsync("/oauth/token", formContent);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadFromJsonAsync<JsonDocument>();
        json!.RootElement.GetProperty("error").GetString().Should().Be("invalid_grant");
    }

    [Fact]
    public async Task TokenEndpoint_UnsupportedGrantType_Returns400()
    {
        using var client = fixture.Factory.CreateClient();

        var formContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
        });

        var response = await client.PostAsync("/oauth/token", formContent);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadFromJsonAsync<JsonDocument>();
        json!.RootElement.GetProperty("error").GetString().Should().Be("unsupported_grant_type");
    }

    [Fact]
    public async Task CliMetadataDocument_ReturnsValidJson()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/oauth/clients/cli.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonDocument>();
        json!.RootElement.GetProperty("client_id").GetString().Should().Contain("/oauth/clients/cli.json");
        json.RootElement.GetProperty("client_name").GetString().Should().Be("Connapse CLI");
    }

    /// <summary>
    /// With AllowAnonymousDiscovery=false (default), an unauthenticated POST to /mcp
    /// should return 401. The MCP SDK uses Streamable HTTP transport (POST-based),
    /// so we use the same POST approach as McpDiscoveryTests.
    /// </summary>
    [Fact]
    public async Task McpEndpoint_NoAuth_Returns401WithWwwAuthenticate()
    {
        using var client = fixture.Factory.CreateClient();

        var json = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-11-05",
                capabilities = new { },
                clientInfo = new { name = "oauth-test", version = "1.0.0" }
            },
            id = "1"
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Should().NotBeEmpty();
        var wwwAuth = response.Headers.WwwAuthenticate.ToString();
        wwwAuth.Should().Contain("Bearer");
        wwwAuth.Should().Contain("resource_metadata");
        wwwAuth.Should().Contain(".well-known/oauth-protected-resource");
    }
}
