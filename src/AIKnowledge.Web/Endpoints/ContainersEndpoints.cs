using AIKnowledge.Core;
using AIKnowledge.Core.Interfaces;
using AIKnowledge.Core.Utilities;
using Microsoft.AspNetCore.Mvc;

namespace AIKnowledge.Web.Endpoints;

public static class ContainersEndpoints
{
    public static IEndpointRouteBuilder MapContainersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/containers").WithTags("Containers");

        // POST /api/containers - Create container
        group.MapPost("/", async (
            [FromBody] CreateContainerApiRequest request,
            [FromServices] IContainerStore containerStore,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "Container name is required" });

            var normalizedName = request.Name.Trim().ToLowerInvariant();

            if (!PathUtilities.IsValidContainerName(normalizedName))
                return Results.BadRequest(new { error = "Container name must be 2-128 characters, lowercase alphanumeric and hyphens, cannot start or end with a hyphen" });

            var existing = await containerStore.GetByNameAsync(normalizedName, ct);
            if (existing is not null)
                return Results.Conflict(new { error = $"Container '{normalizedName}' already exists" });

            var container = await containerStore.CreateAsync(
                new CreateContainerRequest(normalizedName, request.Description), ct);

            return Results.Created($"/api/containers/{container.Id}", container);
        })
        .WithName("CreateContainer")
        .WithDescription("Create a new container for organizing files");

        // GET /api/containers - List all containers
        group.MapGet("/", async (
            [FromServices] IContainerStore containerStore,
            CancellationToken ct) =>
        {
            var containers = await containerStore.ListAsync(ct);
            return Results.Ok(containers);
        })
        .WithName("ListContainers")
        .WithDescription("List all containers");

        // GET /api/containers/{containerId} - Get container details
        group.MapGet("/{containerId:guid}", async (
            Guid containerId,
            [FromServices] IContainerStore containerStore,
            CancellationToken ct) =>
        {
            var container = await containerStore.GetAsync(containerId, ct);
            return container is not null
                ? Results.Ok(container)
                : Results.NotFound(new { error = $"Container {containerId} not found" });
        })
        .WithName("GetContainer")
        .WithDescription("Get a specific container by ID");

        // DELETE /api/containers/{containerId} - Delete container (must be empty)
        group.MapDelete("/{containerId:guid}", async (
            Guid containerId,
            [FromServices] IContainerStore containerStore,
            CancellationToken ct) =>
        {
            var container = await containerStore.GetAsync(containerId, ct);
            if (container is null)
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            try
            {
                var deleted = await containerStore.DeleteAsync(containerId, ct);
                if (!deleted)
                    return Results.NotFound(new { error = $"Container {containerId} not found" });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            return Results.NoContent();
        })
        .WithName("DeleteContainer")
        .WithDescription("Delete an empty container");

        // POST /api/containers/{containerId}/reindex - Reindex documents in container
        group.MapPost("/{containerId:guid}/reindex", async (
            Guid containerId,
            [FromBody] ContainerReindexRequest? request,
            [FromServices] IContainerStore containerStore,
            [FromServices] IReindexService reindexService,
            CancellationToken ct) =>
        {
            if (!await containerStore.ExistsAsync(containerId, ct))
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            var options = new ReindexOptions
            {
                ContainerId = containerId.ToString(),
                Force = request?.Force ?? false,
                DetectSettingsChanges = request?.DetectSettingsChanges ?? true,
                Strategy = request?.Strategy
            };

            var result = await reindexService.ReindexAsync(options, ct);

            return Results.Ok(new
            {
                batchId = result.BatchId,
                totalDocuments = result.TotalDocuments,
                enqueuedCount = result.EnqueuedCount,
                skippedCount = result.SkippedCount,
                failedCount = result.FailedCount,
                reasonCounts = result.ReasonCounts.ToDictionary(
                    kvp => kvp.Key.ToString(),
                    kvp => kvp.Value),
                message = $"Reindex complete: {result.EnqueuedCount} enqueued, {result.SkippedCount} skipped, {result.FailedCount} failed"
            });
        })
        .WithName("ReindexContainer")
        .WithDescription("Reindex all documents in a container");

        return app;
    }
}

// Request DTOs for container endpoints
public record CreateContainerApiRequest(string Name, string? Description = null);

public record ContainerReindexRequest(
    bool? Force = null,
    bool? DetectSettingsChanges = null,
    ChunkingStrategy? Strategy = null);
