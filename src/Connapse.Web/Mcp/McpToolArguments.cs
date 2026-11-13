using System.Text.Json;

namespace Connapse.Web.Mcp;

/// <summary>
/// Checks a tool call's arguments against the tool's input schema before the call tools run it (CON-346), so an
/// agent learns which field is wrong instead of getting the SDK's generic "An error occurred" text. It covers the
/// keywords the SDK emits for tool parameters (<see cref="Keywords"/>); a test fails if a tool's schema uses another.
/// </summary>
public static class McpToolArguments
{
    /// <summary>The schema keywords this validator understands; format, default and description are hints it ignores.</summary>
    public static readonly IReadOnlySet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "type", "properties", "required", "items", "enum", "format", "default", "description", "additionalProperties", "title",
    };

    /// <summary>Every way the arguments break the schema, each naming the field, such as "ids[2]: expected integer".</summary>
    public static IReadOnlyList<string> Check(JsonElement schema, IReadOnlyDictionary<string, JsonElement> arguments)
    {
        var errors = new List<string>();
        var properties = schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object
            ? p
            : default;
        foreach (string name in Required(schema))
            if (!arguments.ContainsKey(name))
                errors.Add($"{name}: required");
        foreach (var (name, value) in arguments)
        {
            if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var property))
                Check(property, value, name, errors);
            else
                errors.Add($"{name}: not a parameter of this tool");
        }
        return errors;
    }

    private static void Check(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        // A bare true, or an object with no type, accepts anything (the SDK's form for a JsonElement parameter).
        if (schema.ValueKind != JsonValueKind.Object)
            return;
        if (schema.TryGetProperty("type", out var type) && Types(type).ToList() is { Count: > 0 } types && !types.Any(t => Is(t, value)))
        {
            errors.Add($"{path}: expected {string.Join(" or ", types)}, got {Describe(value)}");
            return;
        }
        if (schema.TryGetProperty("enum", out var allowed) && allowed.ValueKind == JsonValueKind.Array
            && !allowed.EnumerateArray().Any(a => JsonElement.DeepEquals(a, value)))
        {
            errors.Add($"{path}: must be one of {string.Join(", ", allowed.EnumerateArray().Select(a => a.GetRawText()))}");
            return;
        }
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            int index = 0;
            foreach (var item in value.EnumerateArray())
                Check(items, item, $"{path}[{index++}]", errors);
        }
        if (value.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (string name in Required(schema))
                if (!value.TryGetProperty(name, out _))
                    errors.Add($"{path}.{name}: required");
            foreach (var member in value.EnumerateObject())
                if (properties.TryGetProperty(member.Name, out var property))
                    Check(property, member.Value, $"{path}.{member.Name}", errors);
        }
    }

    private static IEnumerable<string> Required(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array
            ? required.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!)
            : [];

    private static IEnumerable<string> Types(JsonElement type) => type.ValueKind switch
    {
        JsonValueKind.String => [type.GetString()!],
        JsonValueKind.Array => type.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!),
        _ => [],
    };

    private static bool Is(string type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal d) && d == decimal.Truncate(d),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "a string",
        JsonValueKind.Number => $"the number {value.GetRawText()}",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        _ => "null",
    };
}
