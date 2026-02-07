using AIKnowledge.Core;
using AIKnowledge.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AIKnowledge.Web.Endpoints;

public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/search").WithTags("Search");

        // GET /api/search - Simple search with query parameters
        group.MapGet("/", async (
            [FromQuery] string q,
            [FromQuery] string? mode,
            [FromQuery] int? topK,
            [FromQuery] string? containerId,
            [FromServices] IKnowledgeSearch searchService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "Query parameter 'q' is required" });

            var searchMode = Enum.TryParse<SearchMode>(mode, ignoreCase: true, out var parsed)
                ? parsed
                : SearchMode.Hybrid;

            var options = new SearchOptions(
                Mode: searchMode,
                TopK: topK ?? 10,
                ContainerId: containerId);

            var results = await searchService.SearchAsync(q, options, ct);
            return Results.Ok(results);
        })
        .WithName("SimpleSearch")
        .WithDescription("Search knowledge base with query string parameters");

        // POST /api/search - Advanced search with complex filters
        group.MapPost("/", async (
            [FromBody] SearchRequest request,
            [FromServices] IKnowledgeSearch searchService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query))
                return Results.BadRequest(new { error = "Query is required" });

            var options = new SearchOptions(
                Mode: request.Mode ?? SearchMode.Hybrid,
                TopK: request.TopK ?? 10,
                ContainerId: request.ContainerId,
                Filters: request.Filters);

            var results = await searchService.SearchAsync(request.Query, options, ct);
            return Results.Ok(results);
        })
        .WithName("AdvancedSearch")
        .WithDescription("Search knowledge base with advanced filters and options");

        return app;
    }
}

// Request DTO
public record SearchRequest(
    string Query,
    SearchMode? Mode = null,
    int? TopK = null,
    string? ContainerId = null,
    Dictionary<string, string>? Filters = null);
