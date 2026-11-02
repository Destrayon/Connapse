using System.Text.Json.Nodes;
using Connapse.Application;
using Connapse.Web.Endpoints;

namespace Connapse.Web.Services.GenericApi;

/// <summary>A route the committed spec names an operation on, as the spec describes it.</summary>
public sealed record ApiRoute(
    string OperationId,
    string Method,
    string Path,
    string Scope,
    RiskTier Risk,
    string? Summary,
    IReadOnlyList<string> Tags,
    IReadOnlyList<ApiParameter> Parameters,
    JsonNode? Body,
    bool BodyRequired,
    string? BodyContentType)
{
    public bool IsRead => Method == "GET";
}

/// <summary>A path, query or header parameter of a route.</summary>
public sealed record ApiParameter(string Name, string In, bool Required, JsonNode? Schema, string? Description);

/// <summary>
/// The routes of the committed spec (docs/api/openapi.json, embedded in this assembly) whose
/// operation is marked for MCP (CON-20). The generic connapse_api_* tools are built from these; the
/// spec is what they describe to an agent, so they need no code per operation.
/// </summary>
public static class ApiSpec
{
    public const string ResourceName = "Connapse.Web.openapi.json";

    private const int MaxSchemaDepth = 6;

    private static readonly Lazy<IReadOnlyList<ApiRoute>> Loaded = new(Load);

    public static IReadOnlyList<ApiRoute> GenericRoutes => Loaded.Value;

    /// <summary>Whether a caller may reach any read (GET) or any write route the spec offers MCP.</summary>
    public static bool AllowsAny(Func<string, bool> allowsScope, bool reads) =>
        GenericRoutes.Where(r => r.IsRead == reads).Select(r => r.Scope).Distinct(StringComparer.Ordinal).Any(allowsScope);

    private static IReadOnlyList<ApiRoute> Load()
    {
        using var stream = typeof(ApiSpec).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The committed OpenAPI spec isn't embedded as {ResourceName}.");
        return Parse(JsonNode.Parse(stream)!.AsObject());
    }

    public static IReadOnlyList<ApiRoute> Parse(JsonObject spec)
    {
        var schemas = spec["components"]?["schemas"]?.AsObject();
        var routes = new List<ApiRoute>();
        foreach (var (path, item) in spec["paths"]?.AsObject() ?? [])
        {
            if (item is not JsonObject operations)
                continue;
            foreach (var (method, node) in operations)
            {
                if (node is not JsonObject operation
                    || operation[OperationExtensionTransformer.Name] is not JsonObject marked
                    || (string?)marked["mcp"] != nameof(McpExposure.Generic)
                    || !Enum.TryParse((string?)marked["risk"], out RiskTier risk))
                    continue;

                var parameters = (operation["parameters"]?.AsArray() ?? [])
                    .OfType<JsonObject>()
                    .Select(p => new ApiParameter(
                        (string)p["name"]!,
                        (string)p["in"]!,
                        (bool?)p["required"] ?? false,
                        Resolve(p["schema"], schemas, [], 0),
                        (string?)p["description"]))
                    .ToList();

                var content = operation["requestBody"]?["content"]?.AsObject();
                var (bodyType, bodySchema) = content?.FirstOrDefault(c => c.Key == "application/json") is { Key: not null } json
                    ? (json.Key, json.Value?["schema"])
                    : content?.FirstOrDefault() is { Key: not null } other ? (other.Key, other.Value?["schema"]) : (null, null);

                routes.Add(new ApiRoute(
                    (string)marked["id"]!,
                    method.ToUpperInvariant(),
                    path,
                    (string)marked["scope"]!,
                    risk,
                    (string?)operation["summary"] ?? (string?)operation["description"],
                    (operation["tags"]?.AsArray() ?? []).Select(t => (string)t!).ToList(),
                    parameters,
                    bodySchema is null ? null : Resolve(bodySchema, schemas, [], 0),
                    (bool?)operation["requestBody"]?["required"] ?? false,
                    bodyType));
            }
        }
        return routes;
    }

    /// <summary>A schema with its component references written out, so an agent reads one self-contained shape.</summary>
    private static JsonNode? Resolve(JsonNode? schema, JsonObject? schemas, HashSet<string> visiting, int depth)
    {
        switch (schema)
        {
            case JsonObject obj when (string?)obj["$ref"] is { } reference:
                string name = reference[(reference.LastIndexOf('/') + 1)..];
                if (depth >= MaxSchemaDepth || !visiting.Add(name) || schemas?[name] is not { } target)
                    return new JsonObject { ["type"] = "object", ["title"] = name };
                var resolved = Resolve(target, schemas, visiting, depth + 1);
                visiting.Remove(name);
                return resolved;
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (key, value) in obj)
                    copy[key] = Resolve(value, schemas, visiting, depth);
                return copy;
            case JsonArray array:
                return new JsonArray(array.Select(a => Resolve(a, schemas, visiting, depth)).ToArray());
            default:
                return schema?.DeepClone();
        }
    }
}
