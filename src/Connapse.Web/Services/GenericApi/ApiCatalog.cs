using System.Collections.Frozen;
using Connapse.Application;
using Connapse.Web.Endpoints;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Connapse.Web.Services.GenericApi;

/// <summary>A spec route matched to the endpoint that serves it, with the operation that endpoint runs.</summary>
public sealed record ApiCatalogEntry(ApiRoute Route, RouteEndpoint Endpoint, OperationAttribute Operation);

/// <summary>
/// The spec's MCP routes, each matched to its live endpoint (CON-20). A route is kept only when the
/// endpoint it names still runs the same operation and that operation is still marked for MCP, so a
/// spec that lags the code can narrow what the generic tools reach but never widen it.
/// </summary>
public sealed class ApiCatalog(EndpointDataSource endpoints, ILogger<ApiCatalog> logger)
{
    private FrozenDictionary<string, IReadOnlyList<ApiCatalogEntry>>? _byId;

    public IReadOnlyDictionary<string, IReadOnlyList<ApiCatalogEntry>> ById => _byId ??= Build();

    public IReadOnlyList<ApiCatalogEntry> RoutesFor(string operationId) =>
        ById.TryGetValue(operationId, out var routes) ? routes : [];

    private FrozenDictionary<string, IReadOnlyList<ApiCatalogEntry>> Build()
    {
        var live = endpoints.Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => (Key: $"{m} {Normalize(e.RoutePattern)}", Endpoint: e)))
            .GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Endpoint).ToList(), StringComparer.OrdinalIgnoreCase);

        var entries = new List<ApiCatalogEntry>();
        foreach (var route in ApiSpec.GenericRoutes)
        {
            if (!live.TryGetValue($"{route.Method} {route.Path}", out var matched) || matched.Count != 1)
            {
                logger.LogWarning("The spec names {Operation} on {Method} {Path}, which no single endpoint serves; the generic MCP tools leave it out.",
                    route.OperationId, route.Method, route.Path);
                continue;
            }
            var endpoint = matched[0];
            if (EndpointOperations.Find(endpoint.Metadata) is not { Mcp: McpExposure.Generic } operation || operation.Id != route.OperationId)
            {
                logger.LogWarning("The spec names {Operation} on {Method} {Path}, but the endpoint doesn't run it for MCP; the generic MCP tools leave it out.",
                    route.OperationId, route.Method, route.Path);
                continue;
            }
            entries.Add(new ApiCatalogEntry(route, endpoint, operation));
        }
        return entries.GroupBy(e => e.Route.OperationId, StringComparer.Ordinal)
            .ToFrozenDictionary(g => g.Key, g => (IReadOnlyList<ApiCatalogEntry>)g.ToList(), StringComparer.Ordinal);
    }

    /// <summary>A route pattern as the spec writes it: every parameter as {name}, without constraints, defaults or catch-all markers.</summary>
    public static string Normalize(RoutePattern pattern) =>
        "/" + string.Join("/", pattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternSeparatorPart separator => separator.Content,
            RoutePatternParameterPart parameter => $"{{{parameter.Name}}}",
            _ => "",
        }))));
}
