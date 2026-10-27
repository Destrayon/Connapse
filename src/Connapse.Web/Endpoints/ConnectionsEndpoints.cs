using System.Text.Json;
using Connapse.Application;
using Connapse.Application.Connections;
using Connapse.Application.Sources;
using Connapse.Core;
using Connapse.Web.Problems;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

/// <summary>
/// A connection as the API returns it: its settings, never its secret. <c>hasSecret</c> says whether one is stored.
/// </summary>
public sealed record ConnectionResponse(
    Guid Id,
    string Name,
    ConnectionProvider Provider,
    JsonElement? Config,
    bool HasSecret,
    int SourceCount,
    bool SyncPaused,
    ConnectionHealth? Health,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public static ConnectionResponse From(ConnectionView view)
    {
        Connection c = view.Connection;
        return new ConnectionResponse(c.Id, c.Name, c.Provider, ParseConfig(c.ConfigJson), c.HasSecret, c.SourceCount,
            view.SyncPaused, c.Health, c.CreatedAt, c.UpdatedAt);
    }

    private static JsonElement? ParseConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A connection's new name. Its settings and secret are changed elsewhere.</summary>
public sealed record UpdateConnectionApiRequest(string? Name);

/// <summary>Which of a paused connection's sources to turn back on; all of them when <c>sourceIds</c> is left out.</summary>
public sealed record ResumeConnectionRequest(IReadOnlyList<Guid>? SourceIds = null);

/// <summary>
/// Connections over REST, on the same operations as the Connections page: administrators only, every
/// change audited, a delete must name the connection and is refused while sources use it.
/// </summary>
public static class ConnectionsEndpoints
{
    public static IEndpointRouteBuilder MapConnectionsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/connections").WithTags("Connections")
            .RequireAuthorization("RequireSourcesAdmin")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // GET /api/v1/connections?skip=0&take=50&provider=S3
        group.MapGet("/", async (
            [FromQuery] int? skip,
            [FromQuery] int? take,
            [FromQuery] ConnectionProvider? provider,
            [FromServices] ListConnections list,
            HttpContext http,
            CancellationToken ct) =>
        {
            var listed = await list.RunAsync(Rest(http), new ListConnectionsQuery(skip ?? 0, take ?? 50, provider), ct);
            if (!listed.Succeeded)
                return OperationProblems.Result(listed);

            var items = listed.Result!.Items.Select(ConnectionResponse.From).ToList();
            return Results.Ok(new PagedResponse<ConnectionResponse>(items, items.Count, listed.Result.HasMore));
        })
        .Produces<PagedResponse<ConnectionResponse>>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .WithName("connections.list")
        .WithDescription("List connections by name (?skip=0&take=50, optionally ?provider=S3). Never returns a secret.");

        // GET /api/v1/connections/{connectionId}
        group.MapGet("/{connectionId:guid}", async (
            Guid connectionId,
            [FromServices] GetConnection get,
            HttpContext http,
            CancellationToken ct) =>
        {
            var read = await get.RunAsync(Rest(http), new ConnectionRef(connectionId), ct);
            return read.Succeeded ? Results.Ok(ConnectionResponse.From(read.Result!)) : OperationProblems.Result(read);
        })
        .Produces<ConnectionResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("connections.get")
        .WithDescription("Get a connection's settings, health and sync state. Never returns its secret.");

        // PATCH /api/v1/connections/{connectionId} — renames the connection. Audited as connection.updated.
        group.MapPatch("/{connectionId:guid}", async (
            Guid connectionId,
            [FromBody] UpdateConnectionApiRequest request,
            [FromServices] UpdateConnection update,
            HttpContext http,
            CancellationToken ct) =>
        {
            var updated = await update.RunAsync(Rest(http), new ConnectionRef(connectionId), request.Name, ct);
            return updated.Succeeded ? Results.Ok(ConnectionResponse.From(updated.Result!)) : OperationProblems.Result(updated);
        })
        .Produces<ConnectionResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithName("connections.update")
        .WithDescription("Rename a connection. Names are unique across connections.");

        // POST /api/v1/connections/{connectionId}/pause — stops syncing every source on the connection.
        // Nothing is deleted, and the documents stay searchable.
        group.MapPost("/{connectionId:guid}/pause", async (
            Guid connectionId,
            [FromServices] PauseConnection pause,
            HttpContext http,
            CancellationToken ct) =>
        {
            var paused = await pause.RunAsync(Rest(http), new ConnectionRef(connectionId), ct);
            return paused.Succeeded ? Results.Ok(paused.Result) : OperationProblems.Result(paused);
        })
        .Produces<ConnectionSyncState>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithName("connections.pause")
        .WithDescription("Pause syncing for every source on a connection. Nothing is deleted.");

        // POST /api/v1/connections/{connectionId}/resume — turns syncing back on for the sources named
        // in sourceIds, or every source on the connection when it is left out.
        group.MapPost("/{connectionId:guid}/resume", async (
            Guid connectionId,
            [FromBody] ResumeConnectionRequest? request,
            [FromServices] ResumeConnection resume,
            HttpContext http,
            CancellationToken ct) =>
        {
            var resumed = await resume.RunAsync(Rest(http), new ConnectionRef(connectionId), request?.SourceIds, ct);
            return resumed.Succeeded ? Results.Ok(resumed.Result) : OperationProblems.Result(resumed);
        })
        .Produces<ConnectionSyncState>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithName("connections.resume")
        .WithDescription("Resume syncing for the sources named in sourceIds, or every source on the connection.");

        // DELETE /api/v1/connections/{connectionId}?confirmName=... — refused while any source uses it;
        // confirmName must be the connection's name. Audited as connection.deleted.
        group.MapDelete("/{connectionId:guid}", async (
            Guid connectionId,
            [FromQuery] string? confirmName,
            [FromServices] DeleteConnection delete,
            [FromServices] ListSources sources,
            HttpContext http,
            CancellationToken ct) =>
        {
            var deleted = await delete.RunAsync(Rest(http), new ConnectionRef(connectionId), confirmName, ct);
            return deleted.Succeeded ? Results.NoContent() : await Failed(deleted, connectionId, sources, http, ct);
        })
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .Produces<ConnectionInUseProblem>(StatusCodes.Status409Conflict, "application/problem+json")
        .WithName("connections.delete")
        .WithDescription("Delete a connection that no source uses. confirmName must be its name.");

        return app;
    }

    /// <summary>
    /// A refused delete's problem. A connection still in use also lists the sources using it, each with
    /// a link, so a caller can delete them first.
    /// </summary>
    internal static async Task<IResult> Failed(
        Attempt<Connection, ConnectionStatus> deleted, Guid connectionId, ListSources sources, HttpContext http, CancellationToken ct)
    {
        if (deleted.Status != ConnectionStatus.InUse)
            return OperationProblems.Result(deleted);

        var listed = await sources.RunAsync(Rest(http), new ListSourcesQuery(0, ListSources.MaxTake, ConnectionId: connectionId), ct);
        var links = (listed.Result?.Items ?? []).Select(s => new SourceLink(s.Id, s.Name, $"/sources/{s.Id}")).ToList();
        var problem = OperationProblems.For(deleted.Status, deleted.Detail);
        problem.Extensions["sources"] = links;
        return Results.Problem(problem);
    }

    private static OperationContext Rest(HttpContext http) => new(http.User, OperationSurface.Rest);
}
