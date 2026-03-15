using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Connapse.Integration.Tests;

/// <summary>
/// Tests for Mcp:AllowAnonymousDiscovery behavior.
/// Default fixture has the flag OFF (default false).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class McpDiscoveryTests(SharedWebAppFixture fixture)
{
    /// <summary>
    /// When AllowAnonymousDiscovery is false (default), unauthenticated MCP requests
    /// should be rejected.
    /// </summary>
    [Fact]
    public async Task McpEndpoint_DefaultConfig_UnauthenticatedReturns401()
    {
        using var anonClient = fixture.Factory.CreateClient();

        var response = await PostMcpAsync(anonClient, McpInitializeRequest);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static readonly object McpInitializeRequest = new
    {
        jsonrpc = "2.0",
        method = "initialize",
        @params = new
        {
            protocolVersion = "2025-11-05",
            capabilities = new { },
            clientInfo = new { name = "discovery-test", version = "1.0.0" }
        },
        id = "1"
    };

    /// <summary>
    /// Sends a POST to /mcp with the Accept headers required by Streamable HTTP transport.
    /// </summary>
    private static async Task<HttpResponseMessage> PostMcpAsync(HttpClient client, object body)
    {
        var json = JsonSerializer.Serialize(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return await client.SendAsync(request);
    }
}
