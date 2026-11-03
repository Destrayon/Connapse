using ModelContextProtocol.Client;

namespace Connapse.Integration.Tests;

/// <summary>
/// The one way integration tests open an MCP client against the in-process host. The SDK's discover
/// probe has its own 5 s limit, shorter than the shared host can take to answer while other tests load
/// it; past it the test server answers the cancelled probe with 499 and the connect fails (CON-216).
/// This turns that limit off, and the SDK's 60 s initialization timeout still bounds the connect.
/// </summary>
public static class McpTestClient
{
    /// <summary>Connects to the host's /mcp endpoint over streamable HTTP, sending whatever credentials <paramref name="http"/> carries.</summary>
    public static Task<McpClient> ConnectAsync(HttpClient http, bool ownsHttpClient = true) =>
        McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "mcp"), TransportMode = HttpTransportMode.StreamableHttp },
                http,
                ownsHttpClient: ownsHttpClient),
            new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan });
}
