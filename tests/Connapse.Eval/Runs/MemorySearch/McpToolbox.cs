using System.ComponentModel;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Connapse.Web.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Connapse.Eval.Runs.MemorySearch;

/// <summary>
/// The MCP tools an agent gets, called through the very methods the MCP server exposes (<see cref="McpTools"/>), so
/// each tool's name, description, parameters and output are the ones a real agent sees. A parameter with a
/// <see cref="DescriptionAttribute"/> is the agent's; any other is injected as the MCP server would: the request's
/// <see cref="IHttpContextAccessor"/>, the cancellation token, or a service from a scope opened for the call.
/// </summary>
public sealed class McpToolbox : IToolbox
{
    private readonly IServiceProvider _services;
    private readonly ClaimsPrincipal _user;
    private readonly Dictionary<string, MethodInfo> _methods;
    private readonly IReadOnlyDictionary<string, string[]> _pinned;
    private readonly IReadOnlyDictionary<Type, Func<IServiceProvider, object>> _overrides;

    /// <param name="pinned">
    /// Per tool, the parameters the protocol fixes at their defaults: the agent is not offered them, and a value it
    /// sends anyway is ignored.
    /// </param>
    /// <param name="overrides">
    /// Per injected service type, a factory that builds it from the call's scope instead of the container's own
    /// registration, so one arm can run a tool on a variant of an operation (CON-312's unlinked arm).
    /// </param>
    public McpToolbox(IServiceProvider services, ClaimsPrincipal user, IEnumerable<string> toolNames,
        IReadOnlyDictionary<string, string[]>? pinned = null, IReadOnlyDictionary<Type, Func<IServiceProvider, object>>? overrides = null)
    {
        _services = services;
        _user = user;
        _pinned = pinned ?? new Dictionary<string, string[]>();
        _overrides = overrides ?? new Dictionary<Type, Func<IServiceProvider, object>>();
        _methods = toolNames.ToDictionary(n => n, Find, StringComparer.Ordinal);
        Tools = [.. _methods.Select(m => Describe(m.Value, PinnedOf(m.Key)))];
    }

    private string[] PinnedOf(string tool) => _pinned.TryGetValue(tool, out string[]? names) ? names : [];

    public IReadOnlyList<ToolSpec> Tools { get; }

    /// <summary>A reader of every container, as an agent with an Admin token scoped to reading knowledge is.</summary>
    public static ClaimsPrincipal Reader(Guid userId, string name) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
        new Claim(ClaimTypes.Name, name),
        new Claim(ClaimTypes.Role, "Admin"),
        new Claim("scope", "knowledge:read"),
    ], "eval"));

    public static MethodInfo Find(string toolName) =>
        typeof(McpTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName)
        ?? throw new ArgumentException($"No MCP tool is named '{toolName}'.");

    public static ToolSpec Describe(MethodInfo method) => Describe(method, []);

    public static ToolSpec Describe(MethodInfo method, IReadOnlyCollection<string> pinned)
    {
        Dictionary<string, JsonElement> properties = new(StringComparer.Ordinal);
        List<string> required = [];
        foreach (ParameterInfo p in method.GetParameters())
        {
            if (p.GetCustomAttribute<DescriptionAttribute>() is not { } description || pinned.Contains(p.Name!))
                continue;
            properties[p.Name!] = JsonType(p.ParameterType) == "array"
                ? JsonSerializer.SerializeToElement(new { type = "array", items = new { type = JsonType(p.ParameterType.GetElementType()!) }, description = description.Description })
                : JsonSerializer.SerializeToElement(new { type = JsonType(p.ParameterType), description = description.Description });
            if (!p.HasDefaultValue)
                required.Add(p.Name!);
        }
        return new ToolSpec(
            method.GetCustomAttribute<McpServerToolAttribute>()!.Name!,
            method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "",
            properties,
            required);
    }

    public static string JsonType(Type type) => (Nullable.GetUnderlyingType(type) ?? type) switch
    {
        Type t when t == typeof(string) => "string",
        Type t when t == typeof(int) || t == typeof(long) => "integer",
        Type t when t == typeof(float) || t == typeof(double) => "number",
        Type t when t == typeof(bool) => "boolean",
        Type t when t == typeof(string[]) => "array",
        Type t when t == typeof(Guid) => "string",
        Type t when t == typeof(JsonElement) => "object",
        Type t => throw new NotSupportedException($"No JSON type for {t.Name}."),
    };

    /// <summary>
    /// The agent's value as the parameter's type, as the MCP server would bind it: a table id is a UUID string, and a
    /// JSON-object parameter takes the value as sent (the tool itself refuses one that isn't an object).
    /// </summary>
    public static bool TryConvert(JsonElement value, Type type, out object? result)
    {
        result = null;
        if (type == typeof(JsonElement))
        {
            result = value.Clone();
            return true;
        }
        try
        {
            result = value.Deserialize(type);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Expected(Type type) =>
        (Nullable.GetUnderlyingType(type) ?? type) == typeof(Guid) ? "a UUID string" : JsonType(type);

    /// <summary>The agent's arguments without the pinned ones, so each pinned parameter takes its default.</summary>
    public static IReadOnlyDictionary<string, JsonElement> Unpinned(IReadOnlyDictionary<string, JsonElement> input, IReadOnlyCollection<string> pinned) =>
        pinned.Count == 0 ? input : input.Where(a => !pinned.Contains(a.Key)).ToDictionary(StringComparer.Ordinal);

    public async Task<string> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        if (!_methods.TryGetValue(name, out MethodInfo? method))
            return $"Error: Unknown tool '{name}'.";

        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        IHttpContextAccessor http = new FixedHttpContextAccessor(new DefaultHttpContext { User = _user, RequestServices = scope.ServiceProvider });
        input = Unpinned(input, PinnedOf(name));
        ParameterInfo[] parameters = method.GetParameters();
        object?[] args = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            ParameterInfo p = parameters[i];
            if (p.GetCustomAttribute<DescriptionAttribute>() is null)
            {
                args[i] = p.ParameterType == typeof(CancellationToken) ? ct
                    : p.ParameterType == typeof(IHttpContextAccessor) ? http
                    : _overrides.TryGetValue(p.ParameterType, out Func<IServiceProvider, object>? build) ? build(scope.ServiceProvider)
                    : scope.ServiceProvider.GetRequiredService(p.ParameterType);
                continue;
            }
            if (!input.TryGetValue(p.Name!, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            {
                if (!p.HasDefaultValue)
                    return $"Error: Missing required argument '{p.Name}'.";
                args[i] = p.DefaultValue;
                continue;
            }
            if (!TryConvert(value, p.ParameterType, out args[i]))
                return $"Error: Invalid argument '{p.Name}': expected {Expected(p.ParameterType)}.";
        }

        object? result = method.Invoke(null, args);
        return result is Task<string> task ? await task : throw new InvalidOperationException($"{name} did not return Task<string>.");
    }

    private sealed class FixedHttpContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => context; set { } }
    }
}
