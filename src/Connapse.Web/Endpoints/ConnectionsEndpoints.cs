using System.Text.Json;
using Connapse.Application;
using Connapse.Application.Connections;
using Connapse.Application.Ports;
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

/// <summary>
/// A new connection. <c>config</c> holds the provider's settings as the connection returns them; <c>privateKey</c> and
/// <c>passphrase</c> are an SFTP connection's key, and <c>token</c> is a custom source server's. Atlassian sites are
/// added with <c>POST /api/v1/atlassian/sites</c>.
/// </summary>
public sealed record CreateConnectionApiRequest(
    string? Name,
    ConnectionProvider? Provider,
    JsonElement? Config = null,
    string? PrivateKey = null,
    string? Passphrase = null,
    string? Token = null);

/// <summary>A connection that was just created, and what Connapse changed in what was given, if anything.</summary>
public sealed record CreatedConnectionResponse(ConnectionResponse Connection, string? Note);

/// <summary>
/// A connection's new name, settings or both. Settings are checked the way a new connection's are; a secret left out
/// keeps the stored one.
/// </summary>
public sealed record UpdateConnectionApiRequest(
    string? Name,
    JsonElement? Config = null,
    string? PrivateKey = null,
    string? Passphrase = null,
    string? Token = null)
{
    internal ConnectionSettings? Settings() =>
        Config is null && PrivateKey is null && Passphrase is null && Token is null
            ? null
            : new ConnectionSettings(ConfigJsonOf(Config), PrivateKey, Passphrase, Token);

    internal static string? ConfigJsonOf(JsonElement? config) =>
        config is { ValueKind: not JsonValueKind.Null } element ? element.GetRawText() : null;
}

/// <summary>
/// A connection's new secret: <c>privateKey</c> and <c>passphrase</c> for an SFTP connection, <c>token</c> for a custom
/// source server's. Its settings are kept and checked with it.
/// </summary>
public sealed record ReplaceConnectionSecretApiRequest(string? PrivateKey = null, string? Passphrase = null, string? Token = null)
{
    internal ConnectionSecret Secret() => new(PrivateKey, Passphrase, Token);

    // The secret's values stay out of logs and exception messages.
    private bool PrintMembers(System.Text.StringBuilder builder) => false;
}

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

        // POST /api/v1/connections — the page's save checks, then the connection. Audited as connection.created.
        group.MapPost("/", async (
            [FromBody] CreateConnectionApiRequest request,
            [FromServices] CreateConnection create,
            HttpContext http,
            CancellationToken ct) =>
        {
            var settings = new ConnectionSettings(UpdateConnectionApiRequest.ConfigJsonOf(request.Config),
                request.PrivateKey, request.Passphrase, request.Token);
            var created = await create.RunAsync(Rest(http), new NewConnection(request.Name, request.Provider, settings), ct);
            if (!created.Succeeded)
                return OperationProblems.Result(created);

            Connection connection = created.Result!.Connection;
            return Results.Created($"/api/v1/connections/{connection.Id}", new CreatedConnectionResponse(
                ConnectionResponse.From(new ConnectionView(connection, SyncPaused: false)), created.Result.Note));
        })
        .Produces<CreatedConnectionResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status502BadGateway)
        .WithName("connections.create")
        .WithDescription("Create a filesystem, SFTP, S3, Azure Blob, GitHub or custom connection. Its settings are checked "
            + "as the Connections page checks them: a GitHub installation the App still has, an S3 bucket's region, a custom "
            + "source server that accepts its token. Never returns the secret.");

        // PATCH /api/v1/connections/{connectionId} — renames the connection or changes its settings. Audited as connection.updated.
        group.MapPatch("/{connectionId:guid}", async (
            Guid connectionId,
            [FromBody] UpdateConnectionApiRequest request,
            [FromServices] UpdateConnection update,
            HttpContext http,
            CancellationToken ct) =>
        {
            var updated = await update.RunAsync(Rest(http), new ConnectionRef(connectionId),
                new ConnectionChange(request.Name, request.Settings()), ct);
            return updated.Succeeded ? Results.Ok(ConnectionResponse.From(updated.Result!)) : OperationProblems.Result(updated);
        })
        .Produces<ConnectionResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status502BadGateway)
        .WithName("connections.update")
        .WithDescription("Rename a connection or change its settings. Names are unique across connections; settings are "
            + "checked as a new connection's are, and a secret left out keeps the stored one.");

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

        // PUT /api/v1/connections/{connectionId}/secret — the Owner's alone, so it sits outside the group's
        // administrator policy. Audited as connection.credentials_replaced, without the secret.
        app.MapPut("/api/v1/connections/{connectionId:guid}/secret", async (
            Guid connectionId,
            [FromBody] ReplaceConnectionSecretApiRequest request,
            [FromServices] ReplaceConnectionSecret replace,
            HttpContext http,
            CancellationToken ct) =>
        {
            var replaced = await replace.RunAsync(Rest(http), new ConnectionRef(connectionId), request.Secret(), ct);
            return replaced.Succeeded ? Results.Ok(ConnectionResponse.From(replaced.Result!)) : OperationProblems.Result(replaced);
        })
        .WithTags("Connections")
        .RequireAuthorization("RequireSourcesAdmin")
        .Produces<ConnectionResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status502BadGateway)
        .WithName("connections.replaceSecret")
        .WithDescription("Replace an SFTP connection's private key or a custom source server's token. Owner only. The new "
            + "secret is checked with the stored settings before it is kept, and is never returned.");

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
