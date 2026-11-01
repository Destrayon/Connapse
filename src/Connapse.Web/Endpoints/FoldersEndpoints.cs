using System.ComponentModel.DataAnnotations;
using Connapse.Application;
using Connapse.Application.Files;
using Connapse.Core;
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
            HttpContext http,
            Guid containerId,
            [FromBody] CreateFolderRequest request,
            [FromServices] CreateFolder create,
            CancellationToken ct) =>
        {
            var created = await create.RunAsync(Rest(http), containerId, request.Path, ct);
            if (!created.Succeeded)
                return FileProblems.Result(created, request.Path);

            var folder = created.Result!;
            return Results.Created($"/api/containers/{containerId}/folders?path={Uri.EscapeDataString(folder.Path)}", folder);
        })
        .Produces<Folder>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithName("CreateFolder")
        .WithDescription("Create an empty folder in a container");

        // DELETE /api/containers/{containerId}/folders - Delete folder
        group.MapDelete("/", async (
            HttpContext http,
            Guid containerId,
            [FromQuery] string path,
            // Kept so older clients' requests still bind: a folder delete always takes everything inside it.
            [FromQuery] bool cascade,
            [FromServices] DeleteFolder delete,
            CancellationToken ct) =>
        {
            var deleted = await delete.RunAsync(Rest(http), containerId, path, ct);
            return deleted.Succeeded ? Results.NoContent() : FileProblems.Result(deleted, path);
        })
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("DeleteFolder")
        .WithDescription("Delete a folder, optionally cascading to nested files and subfolders");

        return app;
    }

    private static OperationContext Rest(HttpContext http) => new(http.User, OperationSurface.Rest);
}

public record CreateFolderRequest([Required(ErrorMessage = "Folder path is required")] string Path);
