using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Connapse.Web.Mcp;

/// <summary>
/// The search tool and one call tool per risk (CON-346). Splitting the call tool by risk keeps each one's
/// read-only and destructive hints true, so a client that asks before destructive calls still does, and a tool
/// that changes data can never run through <c>call_read</c>.
/// </summary>
[McpServerToolType]
public static class McpSearchTools
{
    private const string ToolDescription = "The exact tool name find_tools gave. Never invent one.";
    private const string ArgumentsDescription = "The tool's arguments as a JSON object, using exactly the names and types of the schema find_tools gave.";

    [McpServerTool(Name = McpToolSearch.FindName, ReadOnly = true, Idempotent = true),
     Authorize(Policy = SignedInRequirement.Policy),
     McpMeta("anthropic/alwaysLoad", true),
     Description("Finds the Connapse tools this token may use that aren't in your tool list. Returns the best matches "
        + "with their arguments schema and the call tool that runs each.")]
    public static async Task<string> FindTools(
        RequestContext<CallToolRequestParams> context,
        McpToolSearch search,
        [Description("Words describing what to do, such as 'delete a container' or 'remember a fact', or a tool's exact name. Empty lists every tool.")] string? query = null,
        [Description("How many matches to list (1-50).")] int limit = 10,
        CancellationToken ct = default)
    {
        var found = await search.FindAsync(context, query, ct);
        return search.Describe(found, query, limit, context.User);
    }

    [McpServerTool(Name = McpToolSearch.CallReadName, ReadOnly = true, Idempotent = true),
     Authorize(Policy = SignedInRequirement.Policy),
     McpMeta("anthropic/alwaysLoad", true),
     Description("Runs a tool find_tools marked Read. It refuses any tool that changes data.")]
    public static Task<CallToolResult> CallRead(
        RequestContext<CallToolRequestParams> context,
        McpToolSearch search,
        [Description(ToolDescription)] string tool,
        [Description(ArgumentsDescription)] JsonElement? arguments = null,
        CancellationToken ct = default) =>
        search.CallAsync(context, McpToolSearch.CallReadName, tool, arguments, ct);

    [McpServerTool(Name = McpToolSearch.CallWriteName, Destructive = false),
     Authorize(Policy = SignedInRequirement.Policy),
     McpMeta("anthropic/alwaysLoad", true),
     Description("Runs a tool find_tools marked Write: one that creates or changes data without removing it.")]
    public static Task<CallToolResult> CallWrite(
        RequestContext<CallToolRequestParams> context,
        McpToolSearch search,
        [Description(ToolDescription)] string tool,
        [Description(ArgumentsDescription)] JsonElement? arguments = null,
        CancellationToken ct = default) =>
        search.CallAsync(context, McpToolSearch.CallWriteName, tool, arguments, ct);

    [McpServerTool(Name = McpToolSearch.CallDestructiveName, Destructive = true),
     Authorize(Policy = SignedInRequirement.Policy),
     McpMeta("anthropic/alwaysLoad", true),
     Description("Runs a tool find_tools marked Destructive or Credential: one that deletes data or changes keys. "
        + "The call may first need a person's approval at /approvals.")]
    public static Task<CallToolResult> CallDestructive(
        RequestContext<CallToolRequestParams> context,
        McpToolSearch search,
        [Description(ToolDescription)] string tool,
        [Description(ArgumentsDescription)] JsonElement? arguments = null,
        CancellationToken ct = default) =>
        search.CallAsync(context, McpToolSearch.CallDestructiveName, tool, arguments, ct);
}

/// <summary>
/// The search and call tools need only a signed-in caller; each call tool then checks the tool it runs. A
/// requirement of its own, rather than a bare [Authorize], lets anonymous discovery list them for a registry.
/// </summary>
public sealed class SignedInRequirement : AuthorizationHandler<SignedInRequirement>, IAuthorizationRequirement
{
    public const string Policy = "Mcp:ToolSearch";

    public static AuthorizationBuilder AddToolSearchPolicy(AuthorizationBuilder builder) =>
        builder.AddPolicy(Policy, policy => policy.AddRequirements(new SignedInRequirement()));

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SignedInRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated == true)
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
