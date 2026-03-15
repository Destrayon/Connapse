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
    /// Spins up a second app instance with AllowAnonymousDiscovery = true.
    /// Verifies that tools/call is rejected for unauthenticated clients.
    /// </summary>
    [Fact]
    public async Task McpToolsCall_AnonDiscoveryEnabled_UnauthenticatedReturnsError()
    {
        await using var anonFactory = CreateFactoryWithAnonDiscovery();
        using var anonClient = anonFactory.CreateClient();

        // First initialize the MCP session
        var initResponse = await PostMcpAsync(anonClient, McpInitializeRequest);
        initResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "initialize should succeed when anonymous discovery is enabled");

        // Extract session ID for subsequent requests
        var sessionId = initResponse.Headers.TryGetValues("Mcp-Session-Id", out var values)
            ? values.FirstOrDefault() : null;

        // Now try to call a tool — should be rejected by the CallToolFilter
        var callToolRequest = new
        {
            jsonrpc = "2.0",
            method = "tools/call",
            @params = new
            {
                name = "container_list",
                arguments = new { }
            },
            id = "2"
        };

        var callResponse = await PostMcpAsync(anonClient, callToolRequest, sessionId);

        // The MCP SDK returns 200 with an error payload in the JSON-RPC response,
        // because the CallToolFilter returns a CallToolResult with IsError=true
        // rather than short-circuiting the HTTP response.
        callResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await callResponse.Content.ReadAsStringAsync();
        body.Should().Contain("Authentication required");
    }

    private WebApplicationFactory<Program> CreateFactoryWithAnonDiscovery()
    {
        return fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Mcp:AllowAnonymousDiscovery", "true");
        });
    }

    /// <summary>
    /// Sends a POST to /mcp with the Accept headers required by Streamable HTTP transport.
    /// </summary>
    private static async Task<HttpResponseMessage> PostMcpAsync(
        HttpClient client, object body, string? sessionId = null)
    {
        var json = JsonSerializer.Serialize(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (sessionId is not null)
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        return await client.SendAsync(request);
    }
}
