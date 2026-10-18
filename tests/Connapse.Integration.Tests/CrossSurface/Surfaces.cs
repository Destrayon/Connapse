using System.Net;
using System.Net.Http.Headers;
using ModelContextProtocol.Protocol;
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

    /// <summary>
    /// What an action's response means, in words every surface shares: "ok", or "error:kind". REST
    /// says it with a status code and MCP with an error flag or an "Error:" message, so comparing
    /// either surface's raw response would make every equivalent pair differ. Payloads are left out
    /// on purpose: each surface formats them its own way, and what the action changed is in the
    /// captured state, audit rows and jobs.
    /// </summary>
    public static string Result(HttpResponseMessage response) => response.StatusCode switch
    {
        _ when response.IsSuccessStatusCode => "ok",
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => "error:validation",
        HttpStatusCode.Unauthorized => "error:unauthenticated",
        HttpStatusCode.Forbidden => "error:forbidden",
        HttpStatusCode.NotFound => "error:not_found",
        HttpStatusCode.Conflict => "error:conflict",
        _ => $"error:status_{(int)response.StatusCode}",
    };

    /// <inheritdoc cref="Result(HttpResponseMessage)"/>
    public static string Result(CallToolResult result)
    {
        string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        if (result.IsError != true && !text.StartsWith("Error:", StringComparison.Ordinal))
            return "ok";
        return text switch
        {
            _ when text.Contains("already exists", StringComparison.OrdinalIgnoreCase) => "error:conflict",
            _ when text.Contains("not found", StringComparison.OrdinalIgnoreCase) => "error:not_found",
            _ when text.Contains("permission", StringComparison.OrdinalIgnoreCase)
                || text.Contains("forbidden", StringComparison.OrdinalIgnoreCase) => "error:forbidden",
            _ when text.Contains("must", StringComparison.OrdinalIgnoreCase)
                || text.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                || text.Contains("required", StringComparison.OrdinalIgnoreCase) => "error:validation",
            _ => "error:unknown",
        };
    }
}
