using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Connapse.Web.Services.GenericApi;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace Connapse.Web.Mcp;

/// <summary>
/// Three tools that reach every API operation marked for MCP, built from the committed spec rather
/// than written one per operation (CON-20). Search describes the operations the caller's token may
/// use; read and write call one through its REST route. A Destructive or Credential call from a token
/// that requires approval is held by <see cref="McpApprovalGate"/> like any other tool of that risk.
/// </summary>
[McpServerToolType]
public static class McpApiTools
{
    public const string SearchName = "connapse_api_search";
    public const string ReadName = "connapse_api_read";
    public const string WriteName = "connapse_api_write";

    /// <summary>The operations described in full; the rest of a search's matches are listed one line each.</summary>
    private const int Detailed = 3;

    [McpServerTool(Name = SearchName, ReadOnly = true, Idempotent = true),
     Authorize(Policy = GenericApiPolicies.Search),
     Description("Finds Connapse API operations this token may call, with their parameters, body schema and risk, for anything the other tools don't cover (users, agents, settings, connections, providers, audit). Call the operation with connapse_api_read if it is a GET, otherwise connapse_api_write.")]
    public static string Search(
        ApiDispatcher api,
        [Description("Words describing what to do, such as 'agent keys' or 'delete source'. Empty lists every operation.")] string? query = null,
        [Description("How many matches to list (1-50).")] int limit = 10)
    {
        var found = api.Search(query);
        if (found.Count == 0)
            return string.IsNullOrWhiteSpace(query) ? "This token may call no API operations." : $"No operation matches '{query}'. Try other words, or search with none.";

        var text = new StringBuilder();
        foreach (var routes in found.Take(Detailed))
            Describe(text, api, routes);
        var rest = found.Skip(Detailed).Take(Math.Clamp(limit, 1, 50) - Detailed).ToList();
        if (rest.Count > 0)
        {
            text.AppendLine("Also matching (search an id for its parameters):");
            foreach (var routes in rest)
                text.AppendLine($"- {routes[0].Operation.Id}: {string.Join(" | ", routes.Select(r => $"{r.Route.Method} {r.Route.Path}"))} [{routes[0].Operation.Risk}]");
        }
        return text.ToString();
    }

    [McpServerTool(Name = ReadName, ReadOnly = true, Idempotent = true),
     Authorize(Policy = GenericApiPolicies.Read),
     Description("Calls a Connapse API operation that only reads (a GET), by the id connapse_api_search gives. Returns the HTTP status and the response body.")]
    public static Task<string> Read(
        ApiDispatcher api,
        [Description("The operation id, such as 'agents.list'.")] string operation,
        [Description("Path, query and header parameters by name, as a JSON object.")] JsonElement? parameters = null,
        CancellationToken ct = default) =>
        api.CallAsync(operation, read: true, parameters, body: null, ct);

    [McpServerTool(Name = WriteName, Destructive = true),
     Authorize(Policy = GenericApiPolicies.Write),
     Description("Calls a Connapse API operation that changes data (POST, PUT, PATCH or DELETE), by the id connapse_api_search gives. A destructive or credential operation may first need a person's approval at /approvals. Returns the HTTP status and the response body.")]
    public static Task<string> Write(
        ApiDispatcher api,
        [Description("The operation id, such as 'agents.create'.")] string operation,
        [Description("Path, query and header parameters by name, as a JSON object.")] JsonElement? parameters = null,
        [Description("The JSON request body, shaped as connapse_api_search describes.")] JsonElement? body = null,
        CancellationToken ct = default) =>
        api.CallAsync(operation, read: false, parameters, body, ct);

    private static void Describe(StringBuilder text, ApiDispatcher api, IReadOnlyList<ApiCatalogEntry> routes)
    {
        var first = routes[0];
        text.AppendLine($"## {first.Operation.Id}  [{first.Operation.Risk}, scope {first.Operation.Scope}{(api.NeedsApproval(first) ? ", needs approval" : "")}]");
        text.AppendLine($"Call with {(first.Route.IsRead ? ReadName : WriteName)}.");
        foreach (var entry in routes)
        {
            var route = entry.Route;
            text.AppendLine($"{route.Method} {route.Path}{(route.Summary is { } summary ? $" — {summary}" : "")}");
            foreach (var parameter in route.Parameters)
                text.AppendLine($"- {parameter.Name} ({parameter.In}{(parameter.Required ? ", required" : "")}): "
                    + $"{parameter.Schema?.ToJsonString() ?? "any"}{(parameter.Description is { } d ? $" — {d}" : "")}");
            if (route.Body is not null)
                text.AppendLine($"- body{(route.BodyRequired ? " (required)" : "")}: {route.Body.ToJsonString()}");
        }
        text.AppendLine();
    }
}
