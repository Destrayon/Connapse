using System.ComponentModel.DataAnnotations;
using Connapse.Application;
using Connapse.Application.Containers;
using Connapse.Background.Jobs;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Vectors;
using Connapse.Web.Problems;
using Connapse.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Connapse.Web.Endpoints;

public static class ContainersEndpoints
{
    public static IEndpointRouteBuilder MapContainersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/containers").WithTags("Containers")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // POST /api/containers - Create container
        group.MapPost("/", async (
            [FromBody] CreateContainerApiRequest request,
            [FromServices] CreateContainer create,
            HttpContext http,
            CancellationToken ct) =>
        {
            var created = await create.RunAsync(Rest(http), new CreateContainerRequest(request.Name, request.Description), ct);
            return created.Succeeded
                ? Results.Created($"/api/containers/{created.Result!.Id}", created.Result)
                : OperationProblems.Result(created);
        })
        .Produces<Container>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithName("CreateContainer")
        .WithDescription("Create a new container for organizing files")
        .RequireAuthorization("RequireEditor");

        // GET /api/containers - List containers (paginated)
        group.MapGet("/", async (
            [FromQuery] int? skip,
            [FromQuery] int? take,
            [FromServices] ListContainers list,
            HttpContext http,
            CancellationToken ct) =>
        {
            var listed = await list.RunAsync(Rest(http), new ListContainersQuery(skip ?? 0, take ?? 50), ct);
            if (!listed.Succeeded)
                return OperationProblems.Result(listed);
            var items = listed.Result!.Items;
            return Results.Ok(new PagedResponse<Container>(items, items.Count, listed.Result.HasMore));
        })
        .Produces<PagedResponse<Container>>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .WithName("ListContainers")
        .WithDescription("List containers with pagination (?skip=0&take=50)")
        .RequireAuthorization("RequireViewer");

        // GET /api/containers/{containerId} - Get container details
        group.MapGet("/{containerId:guid}", async (
            Guid containerId,
            [FromServices] GetContainer get,
            HttpContext http,
            CancellationToken ct) =>
        {
            // No source fallback. #350's compatibility read projected a migrated source back
            // into the Container shape so pre-migration IDs kept resolving here; it was always
            // scoped to one release, and a source's own representation is served by
            // /api/sources/{id}. Resolving one here would also reintroduce the confusion the
            // epic exists to remove — a caller would get something shaped like a container
            // that cannot be browsed or written to.
            var read = await get.RunAsync(Rest(http), ContainerKey.ById(containerId), ct);
            return read.Succeeded ? Results.Ok(read.Result) : OperationProblems.Result(read);
        })
        .Produces<Container>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("GetContainer")
        .WithDescription("Get a specific container by ID")
        .RequireAuthorization("RequireViewer");

        // GET /api/containers/by-name/{name} - Resolve container by name
        group.MapGet("/by-name/{name}", async (
            string name,
            [FromServices] GetContainer get,
            HttpContext http,
            CancellationToken ct) =>
        {
            var read = await get.RunAsync(Rest(http), ContainerKey.ByName(name), ct);
            return read.Succeeded ? Results.Ok(read.Result) : OperationProblems.Result(read);
        })
        .Produces<Container>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("GetContainerByName")
        .WithDescription("Get a specific container by name")
        .RequireAuthorization("RequireViewer");

        // PATCH /api/containers/{containerId} - Rename a container or change its description
        group.MapPatch("/{containerId:guid}", async (
            Guid containerId,
            [FromBody] UpdateContainerApiRequest request,
            [FromServices] RenameContainer rename,
            HttpContext http,
            CancellationToken ct) =>
        {
            var renamed = await rename.RunAsync(Rest(http), containerId, new ContainerChanges(request.Name, request.Description), ct);
            return renamed.Succeeded ? Results.Ok(renamed.Result) : OperationProblems.Result(renamed);
        })
        .Produces<Container>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithName("containers.rename")
        .WithDescription("Rename a container or change its description. Its files stay where they are.")
        // The scope too, so an Editor's knowledge:read token can't write; known-authz-violations.json lists the older routes that don't check it yet.
        .RequireAuthorization("RequireEditor", "Scope:KnowledgeWrite");

        // DELETE /api/containers/{containerId} - Delete container (must be empty)
        group.MapDelete("/{containerId:guid}", async (
            Guid containerId,
            [FromServices] DeleteContainer delete,
            HttpContext http,
            CancellationToken ct) =>
        {
            var deleted = await delete.RunAsync(Rest(http), ContainerKey.ById(containerId), ct);
            return deleted.Succeeded ? Results.NoContent() : OperationProblems.Result(deleted);
        })
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("DeleteContainer")
        .WithDescription("Delete a container. It must be empty first — deleting a container deletes its stored objects.")
        .RequireAuthorization("RequireEditor");

        // GET /api/containers/{containerId}/stats - Get container statistics
        group.MapGet("/{containerId:guid}/stats", async (
            Guid containerId,
            [FromServices] GetContainerStats stats,
            HttpContext http,
            CancellationToken ct) =>
        {
            var read = await stats.RunAsync(Rest(http), ContainerKey.ById(containerId), ct);
            return read.Succeeded ? Results.Ok(ContainerStatsResponse.From(read.Result!)) : OperationProblems.Result(read);
        })
        .Produces<ContainerStatsResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("GetContainerStats")
        .WithDescription("Get statistics for a container: document counts by status, chunk count, storage size, embedding models, last indexed time")
        .RequireAuthorization("RequireViewer");

        // GET /api/containers/{containerId}/settings - Get container settings overrides
        group.MapGet("/{containerId:guid}/settings", async (
            Guid containerId,
            [FromServices] GetContainerSettings settings,
            HttpContext http,
            CancellationToken ct) =>
        {
            var read = await settings.RunAsync(Rest(http), containerId, ct);
            return read.Succeeded ? Results.Ok(read.Result) : OperationProblems.Result(read);
        })
        .Produces<ContainerSettingsOverrides>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("GetContainerSettings")
        .WithDescription("Get per-container settings overrides")
        .RequireAuthorization("RequireViewer");

        // PUT /api/containers/{containerId}/settings - Save container settings overrides
        group.MapPut("/{containerId:guid}/settings", async (
            Guid containerId,
            [FromBody] ContainerSettingsOverrides overrides,
            [FromServices] UpdateContainerSettings update,
            HttpContext http,
            CancellationToken ct) =>
        {
            var saved = await update.RunAsync(Rest(http), containerId, overrides, ct);
            return saved.Succeeded ? Results.Ok(saved.Result) : OperationProblems.Result(saved);
        })
        .Produces<ContainerSettingsOverrides>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("SaveContainerSettings")
        .WithDescription("Save per-container settings overrides")
        .RequireAuthorization("RequireEditor");

        // DELETE /api/containers/{containerId}/settings - Clear every override, back to the global settings
        group.MapDelete("/{containerId:guid}/settings", async (
            Guid containerId,
            [FromServices] ResetContainerSettings reset,
            HttpContext http,
            CancellationToken ct) =>
        {
            var cleared = await reset.RunAsync(Rest(http), containerId, ct);
            return cleared.Succeeded ? Results.NoContent() : OperationProblems.Result(cleared);
        })
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("containers.settings.reset")
        .WithDescription("Clear per-container settings overrides, so the container follows the global settings")
        .RequireAuthorization("RequireEditor", "Scope:KnowledgeWrite");

        // POST /api/containers/{containerId}/reindex - Reindex documents in container
        group.MapPost("/{containerId:guid}/reindex", async (
            Guid containerId,
            [FromBody] ContainerReindexRequest? request,
            [FromServices] ReindexContainer reindex,
            HttpContext http,
            CancellationToken ct) =>
        {
            var ran = await reindex.RunAsync(Rest(http), containerId, new ContainerReindexQuery(
                request?.Force, request?.DetectSettingsChanges, request?.Strategy, request?.DryRun, request?.MaxDocuments, request?.ContinueAfter), ct);
            if (!ran.Succeeded)
                return OperationProblems.Result(ran);

            var result = ran.Result!;
            return Results.Ok(new ContainerReindexResponse(
                BatchId: result.BatchId,
                TotalDocuments: result.TotalDocuments,
                EnqueuedCount: result.EnqueuedCount,
                SkippedCount: result.SkippedCount,
                FailedCount: result.FailedCount,
                PlannedCount: result.PlannedCount,
                RemainingCount: result.RemainingCount,
                ContinueAfter: result.ContinueAfter,
                DryRun: result.DryRun,
                ReasonCounts: result.ReasonCounts.ToDictionary(
                    kvp => kvp.Key.ToString(),
                    kvp => kvp.Value),
                Message: (result.DryRun
                    ? $"Dry run: {result.PlannedCount} would be enqueued, {result.SkippedCount} skipped, {result.FailedCount} failed"
                    : $"Reindex complete: {result.EnqueuedCount} enqueued, {result.SkippedCount} skipped, {result.FailedCount} failed")
                    + (result.ContinueAfter is null
                        ? ""
                        : $"; stopped at maxDocuments with {result.RemainingCount} not yet evaluated, send continueAfter to continue")));
        })
        .Produces<ContainerReindexResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("ReindexContainer")
        .WithDescription("Reindex all documents in a container")
        .RequireAuthorization("RequireEditor");

        // POST /api/containers/{containerId}/retry-failed - Re-enqueue every failed document
        group.MapPost("/{containerId:guid}/retry-failed", async (
            Guid containerId,
            [FromServices] RetryFailedContainerDocuments retry,
            HttpContext http,
            CancellationToken ct) =>
        {
            var retried = await retry.RunAsync(Rest(http), containerId, ct);
            return retried.Succeeded ? Results.Ok(new RetryFailedResponse(retried.Result)) : OperationProblems.Result(retried);
        })
        .Produces<RetryFailedResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("RetryFailedContainerDocuments")
        .WithDescription("Re-enqueue every document in a container whose ingestion failed, with a fresh attempt budget")
        .RequireAuthorization("RequireEditor");

        // POST /api/containers/{containerId}/sync - Reconcile managed storage with the document table
        group.MapPost("/{containerId:guid}/sync", async (
            Guid containerId,
            [FromServices] ReconcileContainer reconcile,
            HttpContext http,
            CancellationToken ct) =>
        {
            var synced = await reconcile.RunAsync(Rest(http), containerId, ct);
            if (!synced.Succeeded)
                return OperationProblems.Result(synced);

            var result = synced.Result!;
            return Results.Ok(new ContainerSyncResponse(
                result.BatchId,
                result.TotalFiles,
                result.EnqueuedCount,
                result.SkippedCount,
                $"Sync complete: {result.EnqueuedCount} enqueued, {result.SkippedCount} skipped"));
        })
        .Produces<ContainerSyncResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("SyncContainer")
        .WithDescription("Reconcile a container's managed storage with the document table. Lists stored objects, compares to DB, enqueues anything new or unfinished.")
        .RequireAuthorization("RequireEditor");

        return app;
    }

    private static OperationContext Rest(HttpContext http) => new(http.User, OperationSurface.Rest);
}

// Request DTOs for container endpoints
public record CreateContainerApiRequest(
    [Required(ErrorMessage = "Container name is required")] string Name,
    string? Description = null);

public record ContainerReindexRequest(
    bool? Force = null,
    bool? DetectSettingsChanges = null,
    ChunkingStrategy? Strategy = null,
    bool? DryRun = null,
    int? MaxDocuments = null,
    string? ContinueAfter = null);

public record UpdateContainerApiRequest(string? Name = null, string? Description = null);

// Response DTOs for container endpoints
public record ContainerStatsResponse(
    string ContainerId,
    string ContainerName,
    ContainerDocumentCounts Documents,
    long TotalChunks,
    long TotalSizeBytes,
    IReadOnlyList<EmbeddingModelInfo> EmbeddingModels,
    DateTime? LastIndexedAt,
    DateTime CreatedAt)
{
    public static ContainerStatsResponse From(ContainerStatsView view) => new(
        view.Container.Id,
        view.Container.Name,
        new ContainerDocumentCounts(view.Stats.DocumentCount, view.Stats.ReadyCount, view.Stats.ProcessingCount, view.Stats.FailedCount),
        view.Stats.TotalChunks,
        view.Stats.TotalSizeBytes,
        view.Models,
        view.Stats.LastIndexedAt,
        view.Container.CreatedAt);
}

public record ContainerDocumentCounts(int Total, int Ready, int Processing, int Failed);

public record ContainerReindexResponse(
    string BatchId,
    int TotalDocuments,
    int EnqueuedCount,
    int SkippedCount,
    int FailedCount,
    int PlannedCount,
    int RemainingCount,
    string? ContinueAfter,
    bool DryRun,
    Dictionary<string, int> ReasonCounts,
    string Message);

public record RetryFailedResponse(int EnqueuedCount);

public record ContainerSyncResponse(
    string BatchId,
    int TotalFiles,
    int EnqueuedCount,
    int SkippedCount,
    string Message);
