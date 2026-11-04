using Connapse.Application;
using Connapse.Application.Memory;
using Connapse.Web.Problems;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

/// <summary>
/// Memory spaces (CON-240): the places memory lives, as the caller may see them — their personal spaces, every team
/// space, and each documents container holding memory — with create, rename, delete and per-person stars.
/// </summary>
public static class MemorySpacesEndpoints
{
    public static IEndpointRouteBuilder MapMemorySpacesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/memory/spaces").WithTags("Memory")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // GET /api/memory/spaces - One page of the spaces the caller may see, with counts
        group.MapGet("/", async (
            [FromQuery] string? kind,
            [FromQuery] string? q,
            [FromQuery] bool? starred,
            [FromQuery] string? sort,
            [FromQuery] int? limit,
            [FromQuery] string? cursor,
            [FromServices] ListMemorySpaces list,
            HttpContext http,
            CancellationToken ct) =>
        {
            MemorySpaceQuery query = new(
                kind is null ? null : Parse<MemorySpaceKind>(kind), q, starred ?? false,
                sort is null ? MemorySpaceSort.Updated : Parse<MemorySpaceSort>(sort), limit, cursor);
            var listed = await list.RunAsync(Rest(http), query, ct);
            return listed.Succeeded ? Results.Ok(listed.Result) : OperationProblems.Result(listed);
        })
        .Produces<MemorySpacePage>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
        .WithName("memorySpaces.list")
        .WithDescription("List the memory spaces you may see, starred first (?kind=personal|team|container&q=&starred=true&sort=updated|name|facts&limit=50&cursor=). "
            + "Each row counts the current facts you may see in it; the counts cover every kind for the same q.")
        .RequireAuthorization("RequireViewer", "Scope:KnowledgeRead");

        // POST /api/memory/spaces - Make a personal or team memory space
        group.MapPost("/", async (
            [FromBody] NewMemorySpace request,
            [FromServices] CreateMemorySpace create,
            HttpContext http,
            CancellationToken ct) =>
        {
            var created = await create.RunAsync(Rest(http), request, ct);
            return created.Succeeded
                ? Results.Created($"/api/memory/spaces/{created.Result!.Id}", created.Result)
                : OperationProblems.Result(created);
        })
        .Produces<MemorySpaceView>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithName("memorySpaces.create")
        .WithDescription("Make a memory space. kind is personal (yours alone) or team (everyone with access); a documents container is made with POST /api/containers.")
        .RequireAuthorization("RequireEditor", "Scope:KnowledgeWrite");

        // PATCH /api/memory/spaces/{spaceId} - Rename a memory space or change its description
        group.MapPatch("/{spaceId:guid}", async (
            Guid spaceId,
            [FromBody] MemorySpaceChanges request,
            [FromServices] UpdateMemorySpace update,
            HttpContext http,
            CancellationToken ct) =>
        {
            var updated = await update.RunAsync(Rest(http), spaceId, request, ct);
            return updated.Succeeded ? Results.Ok(updated.Result) : OperationProblems.Result(updated);
        })
        .Produces<MemorySpaceView>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithName("memorySpaces.update")
        .WithDescription("Rename a memory space or change its description. Facts saved in it stay with it.")
        .RequireAuthorization("RequireEditor", "Scope:KnowledgeWrite");

        // DELETE /api/memory/spaces/{spaceId} - Delete a memory space and every fact in it
        group.MapDelete("/{spaceId:guid}", async (
            Guid spaceId,
            [FromQuery] bool? dryRun,
            [FromServices] DeleteMemorySpace delete,
            HttpContext http,
            CancellationToken ct) =>
        {
            var deleted = await delete.RunAsync(Rest(http), spaceId, dryRun ?? false, ct);
            return deleted.Succeeded ? Results.Ok(deleted.Result) : OperationProblems.Result(deleted);
        })
        .Produces<MemorySpaceDeletion>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
        .WithName("memorySpaces.delete")
        .WithDescription("Delete a memory space and every fact in it, whoever wrote them, and report how many of the facts you may read went, "
            + "with their versions and authors. Needs knowledge:read as well as knowledge:write. ?dryRun=true reports the same counts without deleting.")
        .RequireAuthorization("RequireEditor", "Scope:KnowledgeWrite");

        // PUT /api/memory/spaces/{spaceId}/star - Star a space for yourself
        group.MapPut("/{spaceId:guid}/star", async (
            Guid spaceId,
            [FromServices] StarMemorySpace star,
            HttpContext http,
            CancellationToken ct) =>
        {
            var starred = await star.RunAsync(Rest(http), spaceId, ct);
            return starred.Succeeded ? Results.NoContent() : OperationProblems.Result(starred);
        })
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("memorySpaces.star")
        .WithDescription("Star a memory space or documents container for yourself, so it lists first. Starring it again changes nothing.")
        .RequireAuthorization("RequireViewer", "Scope:KnowledgeRead");

        // DELETE /api/memory/spaces/{spaceId}/star - Remove your star
        group.MapDelete("/{spaceId:guid}/star", async (
            Guid spaceId,
            [FromServices] UnstarMemorySpace unstar,
            HttpContext http,
            CancellationToken ct) =>
        {
            var unstarred = await unstar.RunAsync(Rest(http), spaceId, ct);
            return unstarred.Succeeded ? Results.NoContent() : OperationProblems.Result(unstarred);
        })
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("memorySpaces.unstar")
        .WithDescription("Remove your star from a memory space. Removing one that isn't there changes nothing.")
        .RequireAuthorization("RequireViewer", "Scope:KnowledgeRead");

        return app;
    }

    /// <summary>A query value by name, any case; anything else becomes a value the operation refuses with its field.</summary>
    private static T Parse<T>(string value) where T : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out T parsed) && !int.TryParse(value, out _) ? parsed : (T)(object)(-1);

    private static OperationContext Rest(HttpContext http) => new(http.User, OperationSurface.Rest);
}
