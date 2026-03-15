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

    [Fact]
    public async Task McpToolsList_AnonDiscoveryEnabled_UnauthenticatedReturnsToolSchemas()
    {
        await using var anonFactory = CreateFactoryWithAnonDiscovery();
        using var anonClient = anonFactory.CreateClient();

        // Initialize session first (required by MCP protocol)
        var initResponse = await PostMcpAsync(anonClient, McpInitializeRequest);
        initResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var sessionId = initResponse.Headers.TryGetValues("Mcp-Session-Id", out var values)
            ? values.FirstOrDefault() : null;

        // Request tools/list
        var listRequest = new
        {
            jsonrpc = "2.0",
            method = "tools/list",
            id = "2"
        };

        var response = await PostMcpAsync(anonClient, listRequest, sessionId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        // Verify we get actual tool definitions back
        body.Should().Contain("container_list");
        body.Should().Contain("search_knowledge");
    }

    [Fact]
    public async Task McpToolsCall_AnonDiscoveryEnabled_AuthenticatedAgentSucceeds()
    {
        await using var anonFactory = CreateFactoryWithAnonDiscovery();

        // Create an agent and API key using the admin client from the parent fixture
        var agentResponse = await fixture.AdminClient.PostAsJsonAsync("/api/v1/agents",
            new { name = "mcp-discovery-test-agent", description = "test" });
        agentResponse.EnsureSuccessStatusCode();
        var agent = await agentResponse.Content.ReadFromJsonAsync<JsonElement>();
        var agentId = agent.GetProperty("id").GetString()!;

        var keyResponse = await fixture.AdminClient.PostAsJsonAsync(
            $"/api/v1/agents/{agentId}/keys",
            new { name = "discovery-key", scopes = new[] { "knowledge:read" } });
        keyResponse.EnsureSuccessStatusCode();
        var key = await keyResponse.Content.ReadFromJsonAsync<JsonElement>();
        var apiKeyToken = key.GetProperty("token").GetString()!;

        // Create client against the anon-discovery factory, authenticated via X-Api-Key
        using var agentClient = anonFactory.CreateClient();
        agentClient.DefaultRequestHeaders.Add("X-Api-Key", apiKeyToken);

        // Initialize MCP session
        var initResponse = await PostMcpAsync(agentClient, McpInitializeRequest);
        initResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var sessionId = initResponse.Headers.TryGetValues("Mcp-Session-Id", out var values)
            ? values.FirstOrDefault() : null;

        // Call container_list — should succeed for authenticated agent
        var callRequest = new
        {
            jsonrpc = "2.0",
            method = "tools/call",
            @params = new
            {
                name = "container_list",
                arguments = new { }
            },
            id = "3"
        };

        var response = await PostMcpAsync(agentClient, callRequest, sessionId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("Authentication required");

        // Cleanup
        await fixture.AdminClient.DeleteAsync($"/api/v1/agents/{agentId}");
    }

    [Fact]
    public async Task McpEndpoint_AnonDiscoveryEnabled_RateLimitingStillApplies()
    {
        // Create a factory with anon discovery AND a very low MCP rate limit
        await using var factory = fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Mcp:AllowAnonymousDiscovery", "true");
            builder.UseSetting("RateLimiting:McpPermitLimit", "2");
            builder.UseSetting("RateLimiting:McpWindowSeconds", "60");
        });
        using var client = factory.CreateClient();

        // Send requests until rate limited
        HttpResponseMessage? rateLimited = null;
        for (var i = 0; i < 10; i++)
        {
            var response = await PostMcpAsync(client, McpInitializeRequest);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                rateLimited = response;
                break;
            }
        }

        rateLimited.Should().NotBeNull("expected to hit rate limit within 10 requests with limit of 2");
        rateLimited!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
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
