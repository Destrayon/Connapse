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
    /// What an action's response means, in words every surface shares: "ok" or "error:kind". REST
    /// says it with a status code and a JSON body, MCP with an error flag or an "Error:" message, so
    /// comparing either surface's raw response would make every equivalent pair differ. An id the
    /// response names comes back as "ok id=..." for the capture to check against the resources the
    /// action touched; any other payload (a returned list) is the caller's to project in its act.
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
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or HttpStatusCode.RequestEntityTooLarge
            or HttpStatusCode.UnsupportedMediaType => "error:validation",
        HttpStatusCode.Unauthorized => "error:unauthenticated",
        HttpStatusCode.Forbidden => "error:forbidden",
        HttpStatusCode.NotFound => "error:not_found",
        HttpStatusCode.Conflict => "error:conflict",
        >= HttpStatusCode.InternalServerError => "error:server",
        _ => $"error:status_{(int)response.StatusCode}",
    };

    /// <inheritdoc cref="ResultAsync(HttpResponseMessage)"/>
    public static string Result(CallToolResult result)
    {
        string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        if (result.IsError == true || text.StartsWith("Error:", StringComparison.Ordinal))
            return ErrorKind(text);
        // bulk_upload and bulk_delete answer every call with a summary, failures included:
        // "Uploaded 0 of 1 file(s) ... Failures: ...". All of them is ok; none or some is not.
        if (BulkSummary().Match(text) is { Success: true } bulk)
        {
            int done = int.Parse(bulk.Groups["done"].Value), total = int.Parse(bulk.Groups["total"].Value);
            return done == total ? "ok" : done == 0 ? "error:all_failed" : $"partial:{done}/{total}";
        }
        return McpId().Match(text) is { Success: true } m ? $"ok id={m.Groups[1].Value}" : "ok";
    }

    [GeneratedRegex(@"^(?:Uploaded|Deleted) (?<done>\d+) of (?<total>\d+) file\(s\)")]
    private static partial Regex BulkSummary();

    /// <summary>Phrases MCP's and the UI's validation messages use. McpErrorKindTests keeps every MCP message covered.</summary>
    private static readonly string[] ValidationWords =
        ["must", "invalid", "required", "not supported", "unsupported", "not allowed", "no parser", "provide either", "maximum",
            "exceeds", "empty", "too large", "not writable"];

    /// <summary>
    /// The kind of failure an error message describes, for MCP text and UI alerts alike. What the
    /// caller supplied sits in single quotes (Error: invalid filename 'permission/notes.txt') and is
    /// left out, so a name like that can't turn a validation failure into a permission one.
    /// </summary>
    public static string ErrorKind(string text) => Classify(QuotedInput().Replace(text, "''"));

    [GeneratedRegex(@"'[^']*'")]
    private static partial Regex QuotedInput();

    private static string Classify(string text) => text switch
        {
            _ when text.Contains("already exists", StringComparison.OrdinalIgnoreCase) => "error:conflict",
            _ when text.Contains("not found", StringComparison.OrdinalIgnoreCase) => "error:not_found",
            // REST answers a delete of a non-empty container with 400, a validation failure.
            _ when text.Contains("not empty", StringComparison.OrdinalIgnoreCase) => "error:validation",
            _ when text.Contains("permission", StringComparison.OrdinalIgnoreCase)
                || text.Contains("forbidden", StringComparison.OrdinalIgnoreCase) => "error:forbidden",
            // A document still ingesting, or whose ingestion failed, is in the wrong state: 409 over REST.
            _ when text.Contains("being ingested", StringComparison.OrdinalIgnoreCase)
                || text.Contains("failed ingestion", StringComparison.OrdinalIgnoreCase) => "error:conflict",
            _ when text.Contains("could not be", StringComparison.OrdinalIgnoreCase)
                || text.Contains("upload failed", StringComparison.OrdinalIgnoreCase) => "error:server",
            _ when ValidationWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)) => "error:validation",
            // An exception the tool didn't handle: the SDK answers with this and nothing more.
            _ => "error:unknown",
        };

    /// <summary>
    /// The id line a create answers with: "ID: ..." or "Document ID: ..." alone on its line, so a
    /// UUID inside a filename (File 'notes ID: ....txt') or an indented list entry isn't taken for it.
    /// </summary>
    [GeneratedRegex(@"^(?:Document )?ID: ([0-9a-fA-F-]{36})\r?$", RegexOptions.Multiline)]
    private static partial Regex McpId();
}
