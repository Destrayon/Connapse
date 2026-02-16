using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Connapse.Web.Endpoints;

public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/containers/{containerId:guid}/search").WithTags("Search");

        // GET /api/containers/{containerId}/search - Simple search with query parameters
        group.MapGet("/", async (
            Guid containerId,
            [FromQuery] string q,
            [FromQuery] string? mode,
            [FromQuery] int? topK,
            [FromQuery] string? path,
            [FromQuery] float? minScore,
            [FromServices] IContainerStore containerStore,
            [FromServices] IKnowledgeSearch searchService,
            [FromServices] IOptionsMonitor<SearchSettings> searchSettings,
            CancellationToken ct) =>
        {
            if (!await containerStore.ExistsAsync(containerId, ct))
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "Query parameter 'q' is required" });

            var searchMode = Enum.TryParse<SearchMode>(mode, ignoreCase: true, out var parsed)
                ? parsed
                : SearchMode.Hybrid;

            var filters = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(path))
                filters["pathPrefix"] = path;

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
            Guid containerId,
            [FromBody] ContainerSearchRequest request,
            [FromServices] IContainerStore containerStore,
            [FromServices] IKnowledgeSearch searchService,
            [FromServices] IOptionsMonitor<SearchSettings> searchSettings,
            CancellationToken ct) =>
        {
            if (!await containerStore.ExistsAsync(containerId, ct))
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            if (string.IsNullOrWhiteSpace(request.Query))
                return Results.BadRequest(new { error = "Query is required" });

            var filters = request.Filters ?? new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(request.Path) && !filters.ContainsKey("pathPrefix"))
                filters["pathPrefix"] = request.Path;

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
}

// Request DTO
public record ContainerSearchRequest(
    string Query,
    string? Path = null,
    SearchMode? Mode = null,
    int? TopK = null,
    float? MinScore = null,
    Dictionary<string, string>? Filters = null);
