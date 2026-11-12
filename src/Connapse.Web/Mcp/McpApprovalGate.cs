using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// connapse_api_* call takes the risk of the operation it names (CON-20). A held knowledge deletion is not held that way:
/// it becomes a batch of deletions that Connapse runs itself once the owner approves it (CON-330).
/// </summary>
public sealed class McpApprovalGate(IMcpApprovalStore store, ApiCatalog catalog, RequestDeletions requestDeletions)
{
    private static readonly JsonSerializerOptions BatchJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

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

        if (DeletionItems(tool, operation, context.Params!.Arguments) is { } deletion)
            return await RequestBatchAsync(new OperationContext(user, OperationSurface.Mcp), tool, deletion, ct);

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
                        + $"Ask the person to approve request {request.Id} at /approvals. Call {McpTools.ApprovalStatusName} with requestId {request.Id} "
                        + "(waitSeconds up to 50 waits for the decision) to learn whether they approved; once approved, repeat this exact call with the same arguments. "
                        + $"The request expires at {request.ExpiresAt:u}.",
                },
            ],
        };
    }

    /// <summary>
    /// Asks the owner to approve the held delete as a batch; the agent reads the outcome with deletion_status and never
    /// repeats the call. The batch checks every item now, so a delete that couldn't run is refused here, as the call would be.
    /// </summary>
    private async ValueTask<CallToolResult> RequestBatchAsync(
        OperationContext context, string tool, (DeletionRequestItem[]? Items, string? Problem) deletion, CancellationToken ct)
    {
        if (deletion.Items is not { } items)
            return Error($"Error: {deletion.Problem}");

        var asked = await requestDeletions.ExecuteAsync(context, items, ct);
        if (!asked.Succeeded)
            return Error($"Error: {asked.Detail}");

        DeletionBatchView batch = asked.Result!;
        string summary = $"'{tool}' is pending approval: it deletes data, so your owner must approve it first. "
            + $"It became deletion batch {batch.Id}, which Connapse deletes itself once approved, so don't repeat this call. "
            + $"Ask the person to approve it at /approvals, then call {McpTools.DeletionStatusName} with batchId {batch.Id} "
            + "(waitSeconds up to 50 waits for the decision) to learn whether each item was deleted, skipped because it changed, or denied.";
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = summary },
                new TextContentBlock { Text = JsonSerializer.Serialize(batch, BatchJson) },
            ],
        };
    }

    /// <summary>
    /// The batch a held call would delete, or null when the call is not a knowledge deletion. Credential and admin calls
    /// keep the repeat-the-call approval, since their secrets are never stored readable for Connapse to run them later.
    /// </summary>
    internal static (DeletionRequestItem[]? Items, string? Problem)? DeletionItems(
        string tool, string? operation, IDictionary<string, JsonElement>? arguments)
    {
        string reason = $"Held from the agent's {operation ?? tool} call.";
        switch (operation ?? tool)
        {
            case "delete_file" when string.IsNullOrEmpty(Text(arguments, "fileIds")):
                return ([new DeletionRequestItem("file", Text(arguments, "containerId") ?? "", Text(arguments, "fileId"), reason)], null);

            // delete_file with a list (CON-344), or its deprecated alias bulk_delete.
            case "delete_file":
            case "bulk_delete":
                {
                    if (!string.IsNullOrEmpty(Text(arguments, "fileId")))
                        return (null, "Provide either fileId or fileIds, not both.");
                    List<string>? ids;
                    try
                    {
                        ids = Text(arguments, "fileIds") is { } json ? JsonSerializer.Deserialize<List<string>>(json) : null;
                    }
                    catch (JsonException)
                    {
                        ids = null;
                    }
                    if (ids is null || ids.Count == 0)
                        return (null, "fileIds must be a JSON array of file ids, e.g. [\"id1\",\"id2\"].");
                    string container = Text(arguments, "containerId") ?? "";
                    return (ids.Select(id => new DeletionRequestItem("file", container, id, reason)).ToArray(), null);
                }

            case "container_delete":
                return ([new DeletionRequestItem("container", Text(arguments, "containerId") ?? "", null, reason)], null);

            case "memory_forget":
                return ([new DeletionRequestItem("fact", "", Text(arguments, "factId"), reason)], null);

            case "folders.delete":
                {
                    var parameters = arguments is not null && arguments.TryGetValue("parameters", out var value) && value.ValueKind == JsonValueKind.Object
                        ? value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal)
                        : null;
                    return ([new DeletionRequestItem("folder", Text(parameters, "containerId") ?? "", Text(parameters, "path") ?? "/", reason)], null);
                }

            default:
                return null;
        }
    }

    private static string? Text(IDictionary<string, JsonElement>? arguments, string name) =>
        arguments is null || !arguments.TryGetValue(name, out var value) ? null
            : value.ValueKind == JsonValueKind.String ? value.GetString()
            : value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null
            : value.GetRawText();

    private static CallToolResult Error(string text) => new() { Content = [new TextContentBlock { Text = text }], IsError = true };

    private RiskTier? Risk(string tool, string? operation) =>
        operation is not null
            ? catalog.RoutesFor(operation) is [var entry, ..] ? entry.Operation.Risk : null
            : RiskByTool.TryGetValue(tool, out var risk) ? risk.Risk : null;

    private static string? GenericOperation(IDictionary<string, JsonElement>? arguments) =>
        arguments is not null && arguments.TryGetValue("operation", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
