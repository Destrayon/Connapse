using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

public static class FoldersEndpoints
{
    public static IEndpointRouteBuilder MapFoldersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/containers/{containerId:guid}/folders").WithTags("Folders");

        // POST /api/containers/{containerId}/folders - Create folder
        group.MapPost("/", async (
            Guid containerId,
            [FromBody] CreateFolderRequest request,
            [FromServices] IContainerStore containerStore,
            [FromServices] IFolderStore folderStore,
            CancellationToken ct) =>
        {
            if (!await containerStore.ExistsAsync(containerId, ct))
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            if (string.IsNullOrWhiteSpace(request.Path))
                return Results.BadRequest(new { error = "Folder path is required" });

            var normalizedPath = PathUtilities.NormalizeFolderPath(request.Path);

            if (normalizedPath == "/")
                return Results.BadRequest(new { error = "Cannot create root folder" });

            if (await folderStore.ExistsAsync(containerId, normalizedPath, ct))
                return Results.Conflict(new { error = $"Folder '{normalizedPath}' already exists" });

            var folder = await folderStore.CreateAsync(containerId, normalizedPath, ct);
            return Results.Created($"/api/containers/{containerId}/folders?path={normalizedPath}", folder);
        })
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
            if (!await containerStore.ExistsAsync(containerId, ct))
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "Folder path is required" });

            var normalizedPath = PathUtilities.NormalizeFolderPath(path);

            if (!await folderStore.ExistsAsync(containerId, normalizedPath, ct))
                return Results.NotFound(new { error = $"Folder '{normalizedPath}' not found" });

            // Get documents before deletion so we can cancel jobs and clean up storage
            var documents = await documentStore.ListAsync(containerId, pathPrefix: normalizedPath, ct);
            var filePaths = documents.Select(d => d.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();

            // Cancel any in-flight ingestion jobs for documents in this folder
            foreach (var doc in documents)
                await ingestionQueue.CancelJobForDocumentAsync(doc.Id);

            var deleted = await folderStore.DeleteAsync(containerId, normalizedPath, ct);
            if (!deleted)
                return Results.BadRequest(new { error = "Folder is not empty. Use cascade=true to delete contents." });

            // Clean up file storage (best effort)
            foreach (var filePath in filePaths)
            {
                try { await fileSystem.DeleteAsync(filePath, ct); }
                catch { /* File already deleted or not found */ }
            }

            return Results.NoContent();
        })
        .WithName("DeleteFolder")
        .WithDescription("Delete a folder, optionally cascading to nested files and subfolders");

        return app;
    }
}

public record CreateFolderRequest(string Path);
