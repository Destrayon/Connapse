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
    /// <summary>The endpoint that lists every tool directly, so tests of one tool can see it (CON-346).</summary>
    public const string Full = "mcp/full";

    /// <summary>The default endpoint, whose list shows the core tools plus find_tools and the call tools.</summary>
    public const string Compact = "mcp";

    /// <summary>Connects over streamable HTTP, sending whatever credentials <paramref name="http"/> carries.</summary>
    public static Task<McpClient> ConnectAsync(HttpClient http, bool ownsHttpClient = true, string path = Full) =>
        McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, path), TransportMode = HttpTransportMode.StreamableHttp },
                http,
                ownsHttpClient: ownsHttpClient),
            new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan });
}
