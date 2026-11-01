using System.Collections.Frozen;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Connapse.Application;
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
        string canonical = CanonicalArguments(context.Params!.Arguments);
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

        if (await store.TryConsumeAsync(tokenKey, tool, hash, ct))
            return await proceed();

        var request = await store.RequestAsync(tokenKey, owner, tool, hash, canonical, ct);
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

    /// <summary>The arguments with object keys sorted and whitespace dropped, so an identical call hashes the same.</summary>
    public static string CanonicalArguments(IDictionary<string, JsonElement>? arguments)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in (arguments ?? new Dictionary<string, JsonElement>()).OrderBy(a => a.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(key);
                WriteCanonical(writer, value);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
