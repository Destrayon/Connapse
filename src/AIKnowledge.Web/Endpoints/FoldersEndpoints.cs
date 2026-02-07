using AIKnowledge.Core.Interfaces;
using AIKnowledge.Core.Utilities;
using Microsoft.AspNetCore.Mvc;

namespace AIKnowledge.Web.Endpoints;

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
            CancellationToken ct) =>
        {
            if (!await containerStore.ExistsAsync(containerId, ct))
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "Folder path is required" });

            var normalizedPath = PathUtilities.NormalizeFolderPath(path);

            if (!await folderStore.ExistsAsync(containerId, normalizedPath, ct))
                return Results.NotFound(new { error = $"Folder '{normalizedPath}' not found" });

            var deleted = await folderStore.DeleteAsync(containerId, normalizedPath, ct);
            if (!deleted)
                return Results.BadRequest(new { error = "Folder is not empty. Use cascade=true to delete contents." });

            return Results.NoContent();
        })
        .WithName("DeleteFolder")
        .WithDescription("Delete a folder, optionally cascading to nested files and subfolders");

        return app;
    }
}

public record CreateFolderRequest(string Path);
