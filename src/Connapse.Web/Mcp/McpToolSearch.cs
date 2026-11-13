using System.Collections.Frozen;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Application;
using Connapse.Core.Utilities;
using Connapse.Identity.Authentication;
using Connapse.Web.Services.GenericApi;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Connapse.Web.Mcp;

/// <summary>A tool <c>find_tools</c> can find: a Connapse MCP tool, or an API operation run through the generic tools.</summary>
public sealed record McpToolEntry(string Name, RiskTier Risk, string Description, JsonElement InputSchema, McpServerTool? Tool, IReadOnlyList<ApiCatalogEntry>? Routes)
{
    /// <summary>The call tool that runs this tool: the one for its risk, and never <c>call_read</c> for a route that isn't a GET.</summary>
    public string CallTool => McpToolSearch.CallToolFor(Routes is [var first, ..] && !first.Route.IsRead && Risk == RiskTier.Read ? RiskTier.Write : Risk);
}

/// <summary>
/// Server-side tool search (CON-346, design in docs/research/mcp-tool-scaling-2026-11-12.md section 4.1). The tool
/// list shows a few core tools plus <c>find_tools</c> and one call tool per risk; every other tool is found by
/// search and run through the call tool for its risk. A call runs the same tool method a direct call would, after
/// the same authorization and the same approval gate, because the SDK applies neither to a tool run from inside
/// another tool.
/// </summary>
public sealed class McpToolSearch(
    ApiDispatcher api,
    ApiCatalog catalog,
    IAuthorizationPolicyProvider policies,
    IAuthorizationService authorization,
    McpApprovalGate gate,
    ILogger<McpToolSearch> logger)
{
    public const string FindName = "find_tools";
    public const string CallReadName = "call_read";
    public const string CallWriteName = "call_write";
    public const string CallDestructiveName = "call_destructive";

    /// <summary>The search and call tools themselves, which are never found or run through a call tool.</summary>
    public static readonly FrozenSet<string> Names = new[] { FindName, CallReadName, CallWriteName, CallDestructiveName }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The generic API tools, whose operations are found and called by operation id instead.</summary>
    private static readonly FrozenSet<string> Generic = new[] { McpApiTools.SearchName, McpApiTools.ReadName, McpApiTools.WriteName }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> StopWords = new[] { "a", "an", "the", "to", "of", "for", "in", "on", "and", "or", "my", "me", "with", "by", "from", "is", "it", "tool", "tools" }
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The matches shown with their full input schema; the rest are one line each.</summary>
    public const int Detailed = 3;

    public static string CallToolFor(RiskTier risk) => risk switch
    {
        RiskTier.Read => CallReadName,
        RiskTier.Write => CallWriteName,
        _ => CallDestructiveName,
    };

    /// <summary>The tools and API operations this caller may use, best match for the query first.</summary>
    public async Task<IReadOnlyList<McpToolEntry>> FindAsync(RequestContext<CallToolRequestParams> context, string? query, CancellationToken ct)
    {
        string[] words = Words(query);
        string exact = (query ?? "").Trim();
        var entries = new List<McpToolEntry>();
        foreach (var tool in Tools(context))
            if (Entry(tool) is { } entry && await AllowedAsync(tool, context.User, context))
                entries.Add(entry);
        // The dispatcher lists only operations the caller's token has the scope for.
        entries.AddRange(api.Search(null).Select(Entry));

        return entries
            .Select(e => (Entry: e, Score: Score(e, words, exact)))
            .Where(m => words.Length == 0 || m.Score > 0)
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Entry.Name, StringComparer.Ordinal)
            .Select(m => m.Entry)
            .ToList();
    }

    /// <summary>The answer <c>find_tools</c> gives: the best matches in full, then the next ones one line each.</summary>
    public string Describe(IReadOnlyList<McpToolEntry> found, string? query, int limit, ClaimsPrincipal? user)
    {
        if (found.Count == 0)
            return string.IsNullOrWhiteSpace(query)
                ? "This token may use no tools."
                : $"No tool matches '{query}'. Try other words (the server instructions list every tool by area), or call {FindName} with no query to list them all.";

        bool approval = user is not null && McpApprovalClaims.Required(user);
        var text = new StringBuilder();
        text.AppendLine($"Run a tool with the call tool its heading names, passing the tool's exact name as `tool` and its arguments as the `arguments` object.");
        text.AppendLine();
        foreach (var entry in found.Take(Detailed))
        {
            bool needsApproval = approval && entry.Risk is RiskTier.Destructive or RiskTier.Credential;
            text.AppendLine($"## {entry.Name} — {entry.Risk}, run with {entry.CallTool}{(needsApproval ? " (a person must approve it first)" : "")}");
            text.AppendLine(entry.Description);
            text.AppendLine($"arguments schema: {entry.InputSchema.GetRawText()}");
            text.AppendLine();
        }
        var rest = found.Skip(Detailed).Take(Math.Clamp(limit, 1, 50) - Detailed).ToList();
        if (rest.Count > 0)
        {
            text.AppendLine($"Also matching (call {FindName} with a tool's exact name for its arguments):");
            foreach (var entry in rest)
                text.AppendLine($"- {entry.Name} [{entry.Risk}, {entry.CallTool}]: {FirstSentence(entry.Description)}");
        }
        return text.ToString().ReplaceLineEndings("\n").TrimEnd();
    }

    /// <summary>
    /// Runs <paramref name="tool"/> for the call tool named <paramref name="callTool"/>: refuses a tool of another
    /// risk, one the caller may not use and arguments that break its schema, then runs it through the approval gate.
    /// </summary>
    public async Task<CallToolResult> CallAsync(
        RequestContext<CallToolRequestParams> context, string callTool, string? tool, JsonElement? arguments, CancellationToken ct)
    {
        string name = (tool ?? "").Trim();
        if (Names.Contains(name) || Generic.Contains(name))
            return Error($"'{name}' isn't run through a call tool. Call it directly.");
        if (Arguments(arguments) is not { } given)
            return Error("arguments must be a JSON object of the tool's argument names to values.");

        var tools = Tools(context).ToDictionary(t => t.ProtocolTool.Name, StringComparer.Ordinal);
        McpToolEntry? entry = tools.TryGetValue(name, out var found) ? Entry(found) : null;
        if (entry is null && catalog.RoutesFor(name) is { Count: > 0 } routes)
            entry = Entry(routes);
        if (entry is null)
        {
            var closest = (await FindAsync(context, name, ct)).Take(3).Select(e => e.Name).ToList();
            return Error($"There is no tool named '{name}'. Use the exact name {FindName} gives"
                + (closest.Count > 0 ? $"; the closest are {string.Join(", ", closest)}." : "."));
        }

        // A generic API call runs the connapse_api_* tool for its route, which checks the operation's scope and
        // route, and which the approval gate already knows how to weigh.
        McpServerTool? inner = entry.Tool;
        var innerArguments = given;
        if (entry.Routes is [var route, ..])
        {
            string generic = route.Route.IsRead ? McpApiTools.ReadName : McpApiTools.WriteName;
            inner = tools.GetValueOrDefault(generic);
            innerArguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["operation"] = JsonSerializer.SerializeToElement(entry.Name) };
            foreach (var (key, value) in given)
                if (key is "parameters" or "body")
                    innerArguments[key] = value;
                else
                    return Error($"'{entry.Name}' takes arguments as {{\"parameters\": {{...}}, \"body\": {{...}}}}; '{key}' isn't one of them.");
        }
        if (inner is null || !await AllowedAsync(inner, context.User, context))
            return Error($"This token may not use '{entry.Name}'.");
        if (entry.CallTool != callTool)
            return Error($"'{entry.Name}' is a {entry.Risk} tool; run it with {entry.CallTool}, not {callTool}.");
        if (entry.Tool is not null && McpToolArguments.Check(entry.InputSchema, given) is { Count: > 0 } errors)
            return Error($"The arguments don't fit '{entry.Name}': {string.Join("; ", errors)}. Its arguments schema: {entry.InputSchema.GetRawText()}");

        var request = new RequestContext<CallToolRequestParams>(context.Server, context.JsonRpcRequest,
            new CallToolRequestParams { Name = inner.ProtocolTool.Name, Arguments = innerArguments })
        {
            Services = context.Services,
            MatchedPrimitive = inner,
        };
        try
        {
            return await gate.HandleAsync(request, () => inner.InvokeAsync(request, ct), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "{CallTool} failed running {Tool}", callTool, LogSanitizer.Sanitize(entry.Name));
            return Error(e is McpException ? $"'{entry.Name}' failed: {e.Message}" : $"'{entry.Name}' failed.");
        }
    }

    private static IEnumerable<McpServerTool> Tools(RequestContext<CallToolRequestParams> context) =>
        context.Server.ServerOptions.ToolCollection?.ToArray() ?? [];

    /// <summary>The entry for a Connapse tool, or null for one that declares no risk (the search, call and generic tools).</summary>
    public static McpToolEntry? Entry(McpServerTool tool)
    {
        var protocol = tool.ProtocolTool;
        if (Names.Contains(protocol.Name) || Generic.Contains(protocol.Name)
            || tool.Metadata.OfType<McpToolRiskAttribute>().FirstOrDefault() is not { } risk)
            return null;
        return new McpToolEntry(protocol.Name, risk.Risk, protocol.Description ?? "", protocol.InputSchema, tool, null);
    }

    /// <summary>The entry for an API operation: its routes, and arguments shaped as parameters plus an optional body.</summary>
    public static McpToolEntry Entry(IReadOnlyList<ApiCatalogEntry> routes)
    {
        var operation = routes[0].Operation;
        var description = string.Join("\n", routes.Select(r => $"{r.Route.Method} {r.Route.Path}{(r.Route.Summary is { } s ? $" — {s}" : "")}"));
        description += $"\nNeeds the {operation.Scope} scope.";

        var parameters = new JsonObject();
        var required = new List<string>();
        foreach (var parameter in routes.SelectMany(r => r.Route.Parameters).GroupBy(p => p.Name, StringComparer.Ordinal).Select(g => g.First()))
        {
            var schema = parameter.Schema?.DeepClone() as JsonObject ?? [];
            if (parameter.Description is { } d)
                schema["description"] = $"{parameter.In}: {d}";
            parameters[parameter.Name] = schema;
            // A parameter is required only if every route needs it; routes differ in their path parameters.
            if (routes.All(r => r.Route.Parameters.Any(p => p.Name == parameter.Name && p.Required)))
                required.Add(parameter.Name);
        }
        var properties = new JsonObject
        {
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = parameters,
                ["required"] = new JsonArray([.. required.Select(r => (JsonNode)r)]),
            },
        };
        var body = routes.Select(r => r.Route.Body).FirstOrDefault(b => b is not null);
        if (body is not null)
            properties["body"] = body.DeepClone();
        var input = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (routes.All(r => r.Route.BodyRequired))
            input["required"] = new JsonArray("body");
        return new McpToolEntry(operation.Id, operation.Risk, description, JsonSerializer.SerializeToElement(input), null, routes);
    }

    /// <summary>
    /// Whether the caller passes the tool's own authorization, as the SDK's authorization filters weigh it
    /// (ModelContextProtocol.AspNetCore AuthorizationFilterSetup, which has no public helper).
    /// </summary>
    private async Task<bool> AllowedAsync(McpServerTool tool, ClaimsPrincipal? user, object resource)
    {
        var metadata = tool.Metadata;
        if (metadata.OfType<IAllowAnonymous>().Any())
            return true;
        var authorize = metadata.OfType<IAuthorizeData>().ToList();
        var declared = metadata.OfType<AuthorizationPolicy>().ToList();
        var requirements = metadata.OfType<IAuthorizationRequirementData>().SelectMany(r => r.GetRequirements()).ToList();
        if (authorize.Count == 0 && declared.Count == 0 && requirements.Count == 0)
            return true;

        var policy = await AuthorizationPolicy.CombineAsync(policies, authorize, declared);
        if (requirements.Count > 0)
        {
            var builder = new AuthorizationPolicyBuilder();
            if (policy is not null)
                builder.Combine(policy);
            policy = builder.AddRequirements([.. requirements]).Build();
        }
        return policy is null
            || (await authorization.AuthorizeAsync(user ?? new ClaimsPrincipal(new ClaimsIdentity()), resource, policy)).Succeeded;
    }

    private static Dictionary<string, JsonElement>? Arguments(JsonElement? arguments)
    {
        if (arguments is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        // Some clients send the object as a JSON string.
        if (value.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var parsed = JsonDocument.Parse(value.GetString()!);
                value = parsed.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return value.ValueKind == JsonValueKind.Object
            ? value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal)
            : null;
    }

    private static string[] Words(string? query) =>
        (query ?? "").ToLowerInvariant()
            .Split([' ', ',', '.', '/', '-', '_', ':', '?', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1 && !StopWords.Contains(w))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>Name matches weigh most, then parameter names, then the description; an exact name wins outright.</summary>
    private static int Score(McpToolEntry entry, string[] words, string exact)
    {
        if (string.Equals(entry.Name, exact, StringComparison.OrdinalIgnoreCase))
            return 1_000;
        string name = entry.Name.ToLowerInvariant();
        string arguments = ArgumentNames(entry.InputSchema).ToLowerInvariant();
        string description = entry.Description.ToLowerInvariant();
        return words.Sum(w => (name.Contains(w, StringComparison.Ordinal) ? 4 : 0)
            + (arguments.Contains(w, StringComparison.Ordinal) ? 2 : 0)
            + (description.Contains(w, StringComparison.Ordinal) ? 1 : 0));
    }

    private static string ArgumentNames(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            ? string.Join(' ', properties.EnumerateObject().Select(p => p.Name))
            : "";

    private static string FirstSentence(string text)
    {
        string line = text.Split('\n')[0];
        int end = line.IndexOf(". ", StringComparison.Ordinal);
        return end < 0 ? line : line[..(end + 1)];
    }

    private static CallToolResult Error(string text) => new() { Content = [new TextContentBlock { Text = $"Error: {text}" }], IsError = true };
}
