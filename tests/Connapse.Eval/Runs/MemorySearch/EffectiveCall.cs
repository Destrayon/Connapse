using System.Collections.Frozen;
using System.Text.Json;
using Connapse.Web.Mcp;

namespace Connapse.Eval.Runs.MemorySearch;

/// <summary>
/// The tool a traced call actually ran (CON-346). A call tool runs the tool it names with the arguments it carries,
/// and an admin API operation, whether run through a call tool or through <c>connapse_api_read</c>/<c>_write</c>, is
/// named <c>api:&lt;operation id&gt;</c>. Scoring reads every trace through this, so the full and compact lists are
/// scored alike.
/// </summary>
public sealed record EffectiveCall(string Tool, IReadOnlyDictionary<string, JsonElement> Arguments, string Output, bool IsError, bool ViaCallTool)
{
    public const string ApiPrefix = "api:";

    public static readonly FrozenSet<string> CallTools =
        new[] { McpToolSearch.CallReadName, McpToolSearch.CallWriteName, McpToolSearch.CallDestructiveName }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> ApiTools =
        new[] { McpApiTools.ReadName, McpApiTools.WriteName }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The result started with "Error" or was flagged as an error: the tool refused the call or failed. A call tool
    /// that refuses (wrong risk, bad arguments, unknown tool) reports it as an error too.
    /// </summary>
    public bool Failed => IsError || Output.TrimStart().StartsWith("Error", StringComparison.OrdinalIgnoreCase);

    public static EffectiveCall Of(ToolTrace trace)
    {
        string tool = trace.Name;
        IReadOnlyDictionary<string, JsonElement> args = trace.Input;
        bool via = false;
        if (CallTools.Contains(tool) && args.TryGetValue("tool", out JsonElement inner) && inner.ValueKind == JsonValueKind.String)
        {
            tool = inner.GetString()!;
            args = Object(args.GetValueOrDefault("arguments"));
            via = true;
        }
        if (ApiTools.Contains(tool) && args.TryGetValue("operation", out JsonElement op) && op.ValueKind == JsonValueKind.String)
            return new EffectiveCall(ApiPrefix + op.GetString(), args, trace.Output, trace.IsError, via);
        // A call tool runs an operation id as an API operation; a tool name never contains a dot, an operation id does.
        if (via && tool.Contains('.', StringComparison.Ordinal))
            return new EffectiveCall(ApiPrefix + tool, args, trace.Output, trace.IsError, via);
        return new EffectiveCall(tool, args, trace.Output, trace.IsError, via);
    }

    public static IReadOnlyList<EffectiveCall> Of(IEnumerable<ToolTrace> calls) => [.. calls.Select(Of)];

    private static IReadOnlyDictionary<string, JsonElement> Object(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object
            ? value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}
