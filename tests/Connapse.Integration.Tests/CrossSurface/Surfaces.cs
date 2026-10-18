using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace Connapse.Integration.Tests.CrossSurface;

/// <summary>
/// The ways into Connapse, each pointed at the one shared test host. Only one in-process
/// host works per test process (Hangfire's storage is static), so every surface reuses the
/// fixture's host rather than starting its own.
/// </summary>
public static partial class Surfaces
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
    /// What an action's response means, in words every surface shares: "ok id=...", "ok", or
    /// "error:kind". REST says it with a status code and a JSON body, MCP with an error flag or an
    /// "Error:" message and an "ID:" line, so comparing either surface's raw response would make
    /// every equivalent pair differ. The id a response names is kept, so a create that returns the
    /// wrong id differs; any other payload (a returned list) is the caller's to project in its act.
    /// </summary>
    public static async Task<string> ResultAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            return Error(response);
        string body = await response.Content.ReadAsStringAsync();
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("id", out var id))
                return $"ok id={id}";
        }
        catch (JsonException)
        {
            // Not JSON (an empty 204): nothing to name.
        }
        return "ok";
    }

    private static string Error(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => "error:validation",
        HttpStatusCode.Unauthorized => "error:unauthenticated",
        HttpStatusCode.Forbidden => "error:forbidden",
        HttpStatusCode.NotFound => "error:not_found",
        HttpStatusCode.Conflict => "error:conflict",
        _ => $"error:status_{(int)response.StatusCode}",
    };

    /// <inheritdoc cref="ResultAsync(HttpResponseMessage)"/>
    public static string Result(CallToolResult result)
    {
        string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        if (result.IsError != true && !text.StartsWith("Error:", StringComparison.Ordinal))
            return McpId().Match(text) is { Success: true } m ? $"ok id={m.Groups[1].Value}" : "ok";
        return ErrorKind(text);
    }

    /// <summary>The kind of failure an error message describes, for MCP text and UI alerts alike.</summary>
    public static string ErrorKind(string text) => text switch
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

    [GeneratedRegex(@"\bID:\s*([0-9a-fA-F-]{36})")]
    private static partial Regex McpId();
}
