using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;

namespace Connapse.Integration.Tests.Spec;

/// <summary>
/// docs/api/openapi.json as response checks: for a request's method, route and status, whether the spec
/// documents the response and whether its JSON body matches the documented schema (epic #783, P3, CON-8).
/// </summary>
internal sealed partial class CommittedSpec
{
    private readonly Dictionary<string, JsonObject> _operations = new(StringComparer.Ordinal);
    private readonly JsonObject _definitions;
    private readonly ConcurrentDictionary<string, JsonSchema> _schemas = new(StringComparer.Ordinal);

    private CommittedSpec(JsonObject document)
    {
        foreach (var (path, item) in document["paths"]!.AsObject())
            foreach (var (method, operation) in item!.AsObject())
                if (operation is JsonObject op && op["responses"] is JsonObject)
                    _operations[$"{method.ToUpperInvariant()} {Normalize(path)}"] = op;

        // Every schema's components become one $defs block, so a response schema can be evaluated on its own.
        _definitions = JsonNode.Parse(Rewrite(document["components"]?["schemas"]?.ToJsonString() ?? "{}"))!.AsObject();
    }

    public static CommittedSpec Load(string path) => new(JsonNode.Parse(File.ReadAllText(path))!.AsObject());

    /// <summary>The operation's key, as the spec and the endpoint both spell it.</summary>
    public static string Key(string method, string route) => $"{method.ToUpperInvariant()} {Normalize(route)}";

    /// <summary>
    /// Why a response doesn't conform, as a stable short reason (the key's last part), or null when it does.
    /// <paramref name="detail"/> carries the first schema error, for the log only: it varies by payload.
    /// </summary>
    public string? Check(string operationKey, int status, string? contentType, byte[] body, out string? detail)
    {
        detail = null;
        if (!_operations.TryGetValue(operationKey, out var operation))
            return "not in the spec";

        var responses = operation["responses"]!.AsObject();
        if ((responses[status.ToString(System.Globalization.CultureInfo.InvariantCulture)] ?? responses[$"{status / 100}XX"] ?? responses["default"])
            is not JsonObject response)
            return "status not in the spec";

        string? mediaType = contentType?.Split(';')[0].Trim().ToLowerInvariant();
        if (body.Length == 0 || mediaType is null || !(mediaType == "application/json" || mediaType.EndsWith("+json", StringComparison.Ordinal)))
            return null;

        if (response["content"] is not JsonObject content || content.Count == 0)
            return "body not described";
        if ((content[mediaType] ?? content["application/json"]) is not JsonObject media || media["schema"] is not JsonNode schemaNode)
            return "media type not in the spec";

        var schema = _schemas.GetOrAdd($"{operationKey} {status} {mediaType}", _ =>
        {
            var root = JsonNode.Parse(Rewrite(schemaNode.ToJsonString()))!;
            var wrapped = root is JsonObject o ? o : new JsonObject { ["allOf"] = new JsonArray(root) };
            wrapped["$defs"] = _definitions.DeepClone();
            return JsonSchema.FromText(wrapped.ToJsonString());
        });

        JsonElement instance;
        try
        {
            instance = JsonDocument.Parse(body).RootElement;
        }
        catch (JsonException ex)
        {
            detail = ex.Message;
            return "body is not JSON";
        }

        var result = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid)
            return null;
        detail = string.Join("; ", (result.Details ?? []).Where(d => d.Errors is { Count: > 0 })
            .Take(3).Select(d => $"{d.InstanceLocation}: {string.Join(", ", d.Errors!.Values)}"));
        return "body doesn't match its schema";
    }

    /// <summary>A route as the spec spells it: no constraints, defaults or catch-all marks, and no trailing slash.</summary>
    public static string Normalize(string route)
    {
        string path = RouteParameter().Replace(route, m => "{" + m.Groups["name"].Value + "}");
        path = "/" + path.Trim('/');
        return path;
    }

    /// <summary>
    /// An OpenAPI schema as plain JSON Schema: component refs point into <c>$defs</c>, and the keywords only
    /// OpenAPI defines, which JSON Schema 2020-12 refuses, are dropped. A discriminator only names the member
    /// that picks among the anyOf's choices, which validate on their own.
    /// </summary>
    private static string Rewrite(string json)
    {
        var node = JsonNode.Parse(json.Replace("#/components/schemas/", "#/$defs/", StringComparison.Ordinal));
        Strip(node);
        return node?.ToJsonString() ?? "{}";
    }

    private static readonly string[] OpenApiOnly = ["discriminator", "example", "xml", "externalDocs"];

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (string keyword in OpenApiOnly)
                    o.Remove(keyword);
                foreach (var (_, child) in o)
                    Strip(child);
                break;
            case JsonArray a:
                foreach (var child in a)
                    Strip(child);
                break;
        }
    }

    [GeneratedRegex(@"\{\**(?<name>[A-Za-z_][A-Za-z0-9_]*)[^}]*\}")]
    private static partial Regex RouteParameter();
}
