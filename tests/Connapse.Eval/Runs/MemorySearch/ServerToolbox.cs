using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Connapse.Eval.Runs.MemorySearch;

/// <summary>
/// The tools of the real Connapse MCP server (CON-346): an MCP client connected to <c>/mcp</c> (the compact list) or
/// <c>/mcp/full</c> (every tool), so an agent sees exactly the tool list, schemas, annotations and server
/// instructions a real client gets, and every call runs through the server's own authorization and approval.
/// </summary>
public sealed class ServerToolbox : IToolbox, IAsyncDisposable
{
    public const string CompactPath = "mcp";
    public const string FullPath = "mcp/full";

    private readonly McpClient _client;
    private readonly IReadOnlyDictionary<string, string[]> _pinned;

    private ServerToolbox(McpClient client, IReadOnlyList<ToolSpec> tools, IReadOnlyDictionary<string, string[]> pinned)
    {
        _client = client;
        Tools = tools;
        _pinned = pinned;
        Instructions = client.ServerInstructions;
    }

    public IReadOnlyList<ToolSpec> Tools { get; }

    public string? Instructions { get; }

    /// <summary>The path a tool list is served on.</summary>
    public static string PathOf(bool full) => full ? FullPath : CompactPath;

    /// <param name="http">A client of the server, signed in; it stays the caller's.</param>
    /// <param name="pinned">
    /// Per tool, the parameters the protocol fixes at their defaults: they are left out of the schema the agent sees,
    /// and a value it sends anyway, directly or through a call tool, is dropped.
    /// </param>
    public static async Task<ServerToolbox> ConnectAsync(HttpClient http, string path,
        IReadOnlyDictionary<string, string[]>? pinned, CancellationToken ct)
    {
        pinned ??= new Dictionary<string, string[]>();
        McpClient client = await Connapse.Integration.Tests.McpTestClient.ConnectAsync(http, ownsHttpClient: false, path).WaitAsync(ct);
        try
        {
            List<ToolSpec> tools = [];
            foreach (McpClientTool tool in await client.ListToolsAsync(cancellationToken: ct))
                tools.Add(Spec(tool.ProtocolTool, pinned.TryGetValue(tool.Name, out string[]? p) ? p : []));
            return new ServerToolbox(client, tools, pinned);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    /// <summary>The tool as the server listed it, without the pinned parameters.</summary>
    public static ToolSpec Spec(Tool tool, IReadOnlyCollection<string> pinned)
    {
        JsonObject wire = JsonSerializer.SerializeToNode(tool, McpJsonUtilities.DefaultOptions)!.AsObject();
        if (pinned.Count > 0 && wire["inputSchema"] is JsonObject schema)
        {
            if (schema["properties"] is JsonObject properties)
                foreach (string name in pinned)
                    properties.Remove(name);
            if (schema["required"] is JsonArray required)
                schema["required"] = new JsonArray([.. required.Where(r => !pinned.Contains(r?.GetValue<string>() ?? "")).Select(r => r!.DeepClone())]);
        }
        Dictionary<string, JsonElement> props = wire["inputSchema"]?["properties"] is JsonObject ps
            ? ps.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal)
            : [];
        List<string> req = wire["inputSchema"]?["required"] is JsonArray rs ? [.. rs.Select(r => r!.GetValue<string>())] : [];
        return new ToolSpec(tool.Name, tool.Description ?? "", props, req, wire);
    }

    /// <summary>The agent's arguments without pinned ones, including those it passes to a pinned tool through a call tool.</summary>
    public IReadOnlyDictionary<string, JsonElement> Unpinned(string name, IReadOnlyDictionary<string, JsonElement> input)
    {
        if (_pinned.TryGetValue(name, out string[]? direct))
            return McpToolbox.Unpinned(input, direct);
        if (!EffectiveCall.CallTools.Contains(name)
            || !input.TryGetValue("tool", out JsonElement t) || t.ValueKind != JsonValueKind.String
            || !_pinned.TryGetValue(t.GetString()!, out string[]? inner)
            || !input.TryGetValue("arguments", out JsonElement args) || args.ValueKind != JsonValueKind.Object)
            return input;
        Dictionary<string, JsonElement> copy = new(input, StringComparer.Ordinal)
        {
            ["arguments"] = JsonSerializer.SerializeToElement(
                args.EnumerateObject().Where(p => !inner.Contains(p.Name)).ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal)),
        };
        return copy;
    }

    public async Task<string> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct) =>
        (await InvokeAsync(name, input, ct)).Text;

    public async Task<ToolOutput> InvokeAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        Dictionary<string, object?> args = Unpinned(name, input).ToDictionary(a => a.Key, a => (object?)a.Value, StringComparer.Ordinal);
        try
        {
            CallToolResult result = await _client.CallToolAsync(name, args, cancellationToken: ct);
            return new ToolOutput(Text(result), result.IsError == true);
        }
        catch (McpException ex)
        {
            // An unknown tool or a protocol error, which a real client shows the agent as a failed call.
            return new ToolOutput($"Error: {ex.Message}", true);
        }
    }

    public static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
