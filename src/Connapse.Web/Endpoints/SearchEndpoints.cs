using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Vectors;
using System.Security.Claims;
using Connapse.Web.Problems;
using Connapse.Web.Services;
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
            [FromQuery] string q,
            [FromQuery] string? mode,
            [FromQuery] int? topK,
            [FromQuery] string? path,
            [FromQuery] float? minScore,
            [FromServices] IContainerStore containerStore,
            [FromServices] IKnowledgeSearch searchService,
            [FromServices] IOptionsMonitor<SearchSettings> searchSettings,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var container = await containerStore.GetAsync(containerId, ct);
            if (container is null)
                return ApiProblem.NotFound($"Container {containerId} not found");

            if (string.IsNullOrWhiteSpace(q))
                return ApiProblem.BadRequest("Query parameter 'q' is required");

            var searchParamError = ValidateSearchParams(q, topK, minScore);
            if (searchParamError is not null) return searchParamError;

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
                Filters: filters.Count > 0 ? filters : null,
                UserId: SearchPrincipal.Resolve(user));

            var results = await searchService.SearchAsync(q, options, ct);
            return Results.Ok(results);
        })
        .Produces<SearchResult>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("SimpleSearch")
        .WithDescription("Search within a container using query string parameters");

        // POST /api/containers/{containerId}/search - Advanced search with complex filters
        group.MapPost("/", async (
            Guid containerId,
            [FromBody] ContainerSearchRequest request,
            [FromServices] IContainerStore containerStore,
            [FromServices] IKnowledgeSearch searchService,
            [FromServices] IOptionsMonitor<SearchSettings> searchSettings,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var container = await containerStore.GetAsync(containerId, ct);
            if (container is null)
                return ApiProblem.NotFound($"Container {containerId} not found");

            if (string.IsNullOrWhiteSpace(request.Query))
                return ApiProblem.BadRequest("Query is required");

            var searchParamError = ValidateSearchParams(request.Query, request.TopK, request.MinScore);
            if (searchParamError is not null) return searchParamError;

            var filters = request.Filters ?? new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(request.Path) && !filters.ContainsKey("pathPrefix"))
                filters["pathPrefix"] = request.Path;

            var effectiveMinScore = request.MinScore ?? (float)searchSettings.CurrentValue.MinimumScore;

            var requestMode = request.Mode ?? SearchMode.Hybrid;

            var options = new SearchOptions(
                Mode: requestMode,
                TopK: request.TopK ?? 10,
                MinScore: effectiveMinScore,
                ContainerId: containerId.ToString(),
                Filters: filters.Count > 0 ? filters : null,
                UserId: SearchPrincipal.Resolve(user));

            var results = await searchService.SearchAsync(request.Query, options, ct);
            return Results.Ok(results);
        })
        .Produces<SearchResult>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("AdvancedSearch")
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

        return app;
    }


    private static IResult? ValidateSearchParams(string query, int? topK, float? minScore)
    {
        if (query.Length > ValidationConstants.MaxQueryLength)
            return ApiProblem.BadRequest("query_too_long", $"Query must not exceed {ValidationConstants.MaxQueryLength} characters");

        if (topK.HasValue && (topK.Value < ValidationConstants.MinTopK || topK.Value > ValidationConstants.MaxTopK))
            return ApiProblem.BadRequest("topk_out_of_range", $"topK must be between {ValidationConstants.MinTopK} and {ValidationConstants.MaxTopK}");

        if (minScore.HasValue && (minScore.Value < ValidationConstants.MinScore || minScore.Value > ValidationConstants.MaxScore))
            return ApiProblem.BadRequest("minscore_out_of_range", $"minScore must be between {ValidationConstants.MinScore:F1} and {ValidationConstants.MaxScore:F1}");

        return null;
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

// Response DTOs
public record ContainerEmbeddingModelsResponse(string CurrentModel, IReadOnlyList<EmbeddingModelUsage> Models);

/// <summary>An embedding model with vectors stored, and whether it is the one configured now.</summary>
public record EmbeddingModelUsage(string ModelId, int Dimensions, long VectorCount, bool IsCurrent)
{
    public static IReadOnlyList<EmbeddingModelUsage> From(IEnumerable<EmbeddingModelInfo> models, string currentModel) =>
        models.Select(m => new EmbeddingModelUsage(
            m.ModelId, m.Dimensions, m.VectorCount,
            string.Equals(m.ModelId, currentModel, StringComparison.OrdinalIgnoreCase))).ToList();
}
