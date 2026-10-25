using System.ComponentModel.DataAnnotations;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Web.Problems;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

public static class FoldersEndpoints
{
    public static IEndpointRouteBuilder MapFoldersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/containers/{containerId:guid}/folders").WithTags("Folders")
            .RequireAuthorization("RequireEditor")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // POST /api/containers/{containerId}/folders - Create folder
        group.MapPost("/", async (
            Guid containerId,
            [FromBody] CreateFolderRequest request,
            [FromServices] IContainerStore containerStore,
            [FromServices] IFolderStore folderStore,
            CancellationToken ct) =>
        {
            var container = await containerStore.GetAsync(containerId, ct);
            if (container is null)
                return ApiProblem.NotFound($"Container {containerId} not found");

            if (string.IsNullOrWhiteSpace(request.Path))
                return ApiProblem.BadRequest("Folder path is required");

            // Reject path traversal attempts
            if (PathUtilities.ContainsPathTraversal(request.Path))
                return ApiProblem.BadRequest("Path must not contain '..' segments");

            var pathSegments = request.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (pathSegments.Length > ValidationConstants.MaxPathDepth)
                return ApiProblem.BadRequest("path_too_deep", $"Path exceeds maximum depth of {ValidationConstants.MaxPathDepth} levels");

            var normalizedPath = PathUtilities.NormalizeFolderPath(request.Path);

            if (normalizedPath == "/")
                return ApiProblem.BadRequest("Cannot create root folder");

            if (await folderStore.ExistsAsync(containerId, normalizedPath, ct))
                return ApiProblem.Conflict($"Folder '{normalizedPath}' already exists");

            var folder = await folderStore.CreateAsync(containerId, normalizedPath, ct);
            return Results.Created($"/api/containers/{containerId}/folders?path={normalizedPath}", folder);
        })
        .Produces<Folder>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithName("CreateFolder")
        .WithDescription("Create an empty folder in a container");

        // DELETE /api/containers/{containerId}/folders - Delete folder
        group.MapDelete("/", async (
            Guid containerId,
            [FromQuery] string path,
            [FromQuery] bool cascade,
            [FromServices] IContainerStore containerStore,
            [FromServices] IFolderStore folderStore,
            [FromServices] IDocumentStore documentStore,
            [FromServices] IKnowledgeFileSystem fileSystem,
            [FromServices] IIngestionQueue ingestionQueue,
            CancellationToken ct) =>
        {
            var container = await containerStore.GetAsync(containerId, ct);
            if (container is null)
                return ApiProblem.NotFound($"Container {containerId} not found");

            if (string.IsNullOrWhiteSpace(path))
                return ApiProblem.BadRequest("Folder path is required");

            var normalizedPath = PathUtilities.NormalizeFolderPath(path);

            if (!await folderStore.ExistsAsync(containerId, normalizedPath, ct))
                return ApiProblem.NotFound($"Folder '{normalizedPath}' not found");

            // Get documents before deletion so we can cancel jobs and clean up storage
            var documents = await documentStore.ListAsync(containerId, pathPrefix: normalizedPath, take: int.MaxValue, ct: ct);
            var filePaths = documents.Select(d => d.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();

            // Cancel any in-flight ingestion jobs for documents in this folder
            foreach (var doc in documents)
                await ingestionQueue.CancelJobForDocumentAsync(doc.Id);

            var deleted = await folderStore.DeleteAsync(containerId, normalizedPath, ct);
            if (!deleted)
                return ApiProblem.BadRequest("Folder is not empty. Use cascade=true to delete contents.");

            // Clean up file storage (best effort)
            foreach (var filePath in filePaths)
            {
                try { await fileSystem.DeleteAsync(filePath, ct); }
                catch { /* File already deleted or not found */ }
            }

            return Results.NoContent();
        })
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("DeleteFolder")
        .WithDescription("Delete a folder, optionally cascading to nested files and subfolders");

        return app;
    }
}

public record CreateFolderRequest([Required(ErrorMessage = "Folder path is required")] string Path);
