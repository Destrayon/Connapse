using Connapse.Application;
using Connapse.Application.Search;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Vectors;
using Connapse.Web.Problems;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Connapse.Web.Endpoints;

public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/containers/{containerId:guid}/search").WithTags("Search")
            .RequireAuthorization("RequireViewer")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // GET /api/containers/{containerId}/search - Simple search with query parameters
        group.MapGet("/", async (
            Guid containerId,
            [FromQuery] string? q,
            [FromQuery] string? mode,
            [FromQuery] int? topK,
            [FromQuery] string? path,
            [FromQuery] float? minScore,
            [FromServices] SearchContainer search,
            HttpContext http,
            CancellationToken ct) =>
        {
            var searchMode = Enum.TryParse<SearchMode>(mode, ignoreCase: true, out var parsed)
                ? parsed
                : SearchMode.Hybrid;
            var searched = await search.RunAsync(Rest(http), containerId,
                new SearchRequest(q, searchMode, topK, minScore, path), ct);
            return searched.Succeeded ? Results.Ok(searched.Result) : OperationProblems.Result(searched);
        })
        .Produces<SearchResult>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("search.containerByQuery")
        .WithDescription("Search within a container using query string parameters");

        // POST /api/containers/{containerId}/search - Advanced search with complex filters
        group.MapPost("/", async (
            Guid containerId,
            [FromBody] SearchApiRequest request,
            [FromServices] SearchContainer search,
            HttpContext http,
            CancellationToken ct) =>
        {
            var searched = await search.RunAsync(Rest(http), containerId, request.ToSearch(), ct);
            return searched.Succeeded ? Results.Ok(searched.Result) : OperationProblems.Result(searched);
        })
        .Produces<SearchResult>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("search.container")
        .WithDescription("Search within a container with advanced filters and options");

        // GET /api/containers/{containerId}/search/models - Get embedding models with vectors
        group.MapGet("/models", async (
            Guid containerId,
            [FromServices] IContainerStore containerStore,
            [FromServices] VectorModelDiscovery modelDiscovery,
            [FromServices] IOptionsMonitor<EmbeddingSettings> embeddingSettings,
            CancellationToken ct) =>
        {
            var container = await containerStore.GetAsync(containerId, ct);
            if (container is null)
                return ApiProblem.NotFound($"Container {containerId} not found");

            var models = await modelDiscovery.GetModelsAsync(containerId, ct);
            var currentModel = EmbeddingIdentity.For(embeddingSettings.CurrentValue);

            return Results.Ok(new ContainerEmbeddingModelsResponse(currentModel, EmbeddingModelUsage.From(models, currentModel)));
        })
        .Produces<ContainerEmbeddingModelsResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("GetEmbeddingModels")
        .WithDescription("Get embedding models with vectors in this container");

        // POST /api/sources/{sourceId}/search - Search a source, as a container is searched (#916)
        app.MapPost("/api/sources/{sourceId:guid}/search", async (
            Guid sourceId,
            [FromBody] SearchApiRequest request,
            [FromServices] SearchSource search,
            HttpContext http,
            CancellationToken ct) =>
        {
            var searched = await search.RunAsync(Rest(http), sourceId, request.ToSearch(), ct);
            return searched.Succeeded ? Results.Ok(searched.Result) : OperationProblems.Result(searched);
        })
        .WithTags("Search")
        .RequireAuthorization("RequireViewer")
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .Produces<SearchResult>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("search.source")
        .WithDescription("Search a source. Every hit is checked for the caller, as a container search's are; a source the caller may not know exists reads as not found.");

        return app;
    }

    private static OperationContext Rest(HttpContext http) => new(http.User, OperationSurface.Rest);
}

/// <summary>A search's body, for a container or a source.</summary>
public record SearchApiRequest(
    string? Query,
    string? Path = null,
    SearchMode? Mode = null,
    int? TopK = null,
    float? MinScore = null,
    Dictionary<string, string>? Filters = null)
{
    public SearchRequest ToSearch() => new(Query, Mode ?? SearchMode.Hybrid, TopK, MinScore, Path, Filters);
}
