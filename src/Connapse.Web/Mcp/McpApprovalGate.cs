using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using Connapse.Application;
using Connapse.Application.Approvals;
using Connapse.Identity.Authentication;
using Connapse.Identity.Services;
using Connapse.Web.Services.GenericApi;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Connapse.Web.Mcp;

/// <summary>
/// Holds a Destructive or Credential MCP tool call until a person approves it (#782). The first call
/// from a token that requires approval records a pending request and says so; once approved on the
/// approvals page, the same call with the same arguments from the same token runs, once. A generic
/// connapse_api_* call takes the risk of the operation it names (CON-20).
/// </summary>
public sealed class McpApprovalGate(IMcpApprovalStore store, ApiCatalog catalog)
{
    private static readonly FrozenDictionary<string, McpToolRiskAttribute> RiskByTool = typeof(McpTools)
        .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
        .Select(m => (Tool: m.GetCustomAttribute<McpServerToolAttribute>(), Risk: m.GetCustomAttribute<McpToolRiskAttribute>()))
        .Where(t => t.Tool?.Name is not null && t.Risk is not null)
        .ToFrozenDictionary(t => t.Tool!.Name!, t => t.Risk!, StringComparer.Ordinal);

    public async ValueTask<CallToolResult> HandleAsync(
        RequestContext<CallToolRequestParams> context, Func<ValueTask<CallToolResult>> proceed, CancellationToken ct)
    {
        string? tool = context.Params?.Name;
        var user = context.User;
        string? operation = tool is McpApiTools.ReadName or McpApiTools.WriteName ? GenericOperation(context.Params!.Arguments) : null;
        if (tool is null || user?.Identity?.IsAuthenticated != true
            || Risk(tool, operation) is not (RiskTier.Destructive or RiskTier.Credential) || !McpApprovalClaims.Required(user))
            return await proceed();

        if (McpApprovalClaims.OwnerUserId(user) is not { } owner)
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = "This call needs approval, but the token has no owner to approve it." }],
                IsError = true,
            };

        string tokenKey = McpApprovalClaims.TokenKey(user);
        // Approving changes nothing about how the call runs: the agent repeats it with its real arguments, and the
        // store only lets it through if they are exactly the ones it sealed. Secrets are never stored readable (CON-185).
        var arguments = McpCallArguments.From(context.Params!.Arguments);

        if (await store.TryConsumeAsync(tokenKey, tool, arguments.Canonical, arguments.Redacted, ct))
            return await proceed();

        var request = await store.RequestAsync(tokenKey, owner, tool, arguments.Canonical, arguments.Redacted, ct);
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = $"'{tool}'{(operation is null ? "" : $" ({operation})")} is pending approval: it changes or removes data, so a person must approve it first. "
                        + $"Approve request {request.Id} at /approvals, then repeat this exact call with the same arguments. "
                        + $"The request expires at {request.ExpiresAt:u}.",
                },
            ],
        };
    }

    private RiskTier? Risk(string tool, string? operation) =>
        operation is not null
            ? catalog.RoutesFor(operation) is [var entry, ..] ? entry.Operation.Risk : null
            : RiskByTool.TryGetValue(tool, out var risk) ? risk.Risk : null;

    private static string? GenericOperation(IDictionary<string, JsonElement>? arguments) =>
        arguments is not null && arguments.TryGetValue("operation", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
