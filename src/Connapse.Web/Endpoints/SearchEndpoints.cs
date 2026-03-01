using System.Security.Claims;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Connapse.Web.Endpoints;

public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/containers/{containerId:guid}/search").WithTags("Search")
            .RequireAuthorization("RequireViewer");

        // GET /api/containers/{containerId}/search - Simple search with query parameters
        group.MapGet("/", async (
            HttpContext httpContext,
            Guid containerId,
            [FromQuery] string q,
            [FromQuery] string? mode,
            [FromQuery] int? topK,
            [FromQuery] string? path,
            [FromQuery] float? minScore,
            [FromServices] IContainerStore containerStore,
            [FromServices] IKnowledgeSearch searchService,
            [FromServices] IOptionsMonitor<SearchSettings> searchSettings,
            [FromServices] ICloudScopeService cloudScopeService,
            CancellationToken ct) =>
        {
            var container = await containerStore.GetAsync(containerId, ct);
            if (container is null)
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            // Cloud scope enforcement
            var scopeResult = await ResolveCloudScope(httpContext, container, cloudScopeService, ct);
            if (scopeResult is { HasAccess: false })
                return CloudAccessDenied(scopeResult, containerId);

            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "Query parameter 'q' is required" });

            var searchMode = Enum.TryParse<SearchMode>(mode, ignoreCase: true, out var parsed)
                ? parsed
                : SearchMode.Hybrid;

            var filters = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(path))
                filters["pathPrefix"] = path;

            // Inject cloud scope as path prefix filter
            InjectScopeFilter(scopeResult, filters);

            var effectiveMinScore = minScore ?? (float)searchSettings.CurrentValue.MinimumScore;

            var options = new SearchOptions(
                Mode: searchMode,
                TopK: topK ?? 10,
                MinScore: effectiveMinScore,
                ContainerId: containerId.ToString(),
                Filters: filters.Count > 0 ? filters : null);

            var results = await searchService.SearchAsync(q, options, ct);
            return Results.Ok(results);
        })
        .WithName("SimpleSearch")
        .WithDescription("Search within a container using query string parameters");

        // POST /api/containers/{containerId}/search - Advanced search with complex filters
        group.MapPost("/", async (
            HttpContext httpContext,
            Guid containerId,
            [FromBody] ContainerSearchRequest request,
            [FromServices] IContainerStore containerStore,
            [FromServices] IKnowledgeSearch searchService,
            [FromServices] IOptionsMonitor<SearchSettings> searchSettings,
            [FromServices] ICloudScopeService cloudScopeService,
            CancellationToken ct) =>
        {
            var container = await containerStore.GetAsync(containerId, ct);
            if (container is null)
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            // Cloud scope enforcement
            var scopeResult = await ResolveCloudScope(httpContext, container, cloudScopeService, ct);
            if (scopeResult is { HasAccess: false })
                return CloudAccessDenied(scopeResult, containerId);

            if (string.IsNullOrWhiteSpace(request.Query))
                return Results.BadRequest(new { error = "Query is required" });

            var filters = request.Filters ?? new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(request.Path) && !filters.ContainsKey("pathPrefix"))
                filters["pathPrefix"] = request.Path;

            // Inject cloud scope as path prefix filter
            InjectScopeFilter(scopeResult, filters);

            var effectiveMinScore = request.MinScore ?? (float)searchSettings.CurrentValue.MinimumScore;

            var options = new SearchOptions(
                Mode: request.Mode ?? SearchMode.Hybrid,
                TopK: request.TopK ?? 10,
                MinScore: effectiveMinScore,
                ContainerId: containerId.ToString(),
                Filters: filters.Count > 0 ? filters : null);

            var results = await searchService.SearchAsync(request.Query, options, ct);
            return Results.Ok(results);
        })
        .WithName("AdvancedSearch")
        .WithDescription("Search within a container with advanced filters and options");

        return app;
    }

    /// <summary>
    /// Injects the first allowed prefix as a pathPrefix filter for cloud-scoped searches.
    /// For full access ("/") or non-cloud containers (null scope), no filter is added.
    /// Multi-prefix search is a known limitation — only the first prefix is used.
    /// </summary>
    private static void InjectScopeFilter(CloudScopeResult? scopeResult, Dictionary<string, string> filters)
    {
        if (scopeResult is null || scopeResult.AllowedPrefixes.Contains("/"))
            return;

        // Only inject if no explicit pathPrefix was already provided by the caller
        if (!filters.ContainsKey("pathPrefix") && scopeResult.AllowedPrefixes.Count > 0)
            filters["pathPrefix"] = scopeResult.AllowedPrefixes[0];
    }

    private static async Task<CloudScopeResult?> ResolveCloudScope(
        HttpContext httpContext, Container container,
        ICloudScopeService cloudScopeService, CancellationToken ct)
    {
        var userId = GetUserId(httpContext);
        if (userId is null) return null;
        return await cloudScopeService.GetScopesAsync(userId.Value, container, ct);
    }

    private static IResult CloudAccessDenied(CloudScopeResult scopeResult, Guid containerId) =>
        Results.Json(new
        {
            error = "cloud_access_denied",
            message = scopeResult.Error ?? "Access denied.",
            containerId = containerId.ToString()
        }, statusCode: 403);

    private static Guid? GetUserId(HttpContext httpContext)
    {
        var idClaim = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(idClaim, out var userId) ? userId : null;
    }
}

// Request DTO
public record ContainerSearchRequest(
    string Query,
    string? Path = null,
    SearchMode? Mode = null,
    int? TopK = null,
    float? MinScore = null,
    Dictionary<string, string>? Filters = null);
