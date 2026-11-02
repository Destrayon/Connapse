using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Connapse.Integration.Tests;

/// <summary>
/// #846: /mcp refuses a browser request from a site other than Connapse's own (DNS rebinding) before
/// authentication, and leaves non-browser clients, which send no Origin, to authentication as before.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class McpOriginValidationTests(SharedWebAppFixture fixture)
{
    private static readonly object Initialize = new
    {
        jsonrpc = "2.0",
        method = "initialize",
        @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "origin-test", version = "1.0.0" } },
        id = "1",
    };

    private async Task<HttpResponseMessage> PostAsync(string? origin)
    {
        using var client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(Initialize) };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Mcp_ForeignOrigin_Returns403WithAJsonRpcError()
    {
        using var response = await PostAsync("http://evil.example.com");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"jsonrpc\":\"2.0\"").And.Contain("Origin not allowed");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://localhost:5173")]
    public async Task Mcp_NoOriginOrLoopbackOrigin_ReachesAuthentication(string? origin)
    {
        using var response = await PostAsync(origin);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the default host requires a credential on /mcp");
    }
}
