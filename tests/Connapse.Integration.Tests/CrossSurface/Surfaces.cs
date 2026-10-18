using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace Connapse.Integration.Tests.CrossSurface;

/// <summary>
/// The ways into Connapse, each pointed at the one shared test host. Only one in-process
/// host works per test process (Hangfire's storage is static), so every surface reuses the
/// fixture's host rather than starting its own.
/// </summary>
public static class Surfaces
{
    /// <summary>The REST API, signed in as the seeded admin.</summary>
    public static HttpClient Rest(SharedWebAppFixture fixture) => fixture.AdminClient;

    /// <summary>The MCP server at /mcp, over the SDK's own client, in-process and signed in as the admin.</summary>
    public static async Task<McpClient> McpAsync(SharedWebAppFixture fixture)
    {
        var http = fixture.Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.AdminToken);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(http.BaseAddress!, "mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }
}
