using System.Text.Json;
using Connapse.Application;
using Connapse.Application.Sources;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Problems;
using Connapse.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

/// <summary>
/// REST surface for sources — external scopes Connapse mirrors but does not own.
/// <para>
/// Two rules shape this file. First, **nothing here returns documents or paths.** There is no
/// browse route and no document listing: a source is a searchable scope, and enumerating what
/// is inside one exposes every synced object to any Connapse reader regardless of whether they
/// could see it in the source system. That leak is the reason epic #348 exists.
/// </para>
/// <para>
/// Second, **mutations require an administrator, not an editor.** Creating a source chooses
/// what external data gets indexed and made searchable, bounded only by what the connection's
/// credential can reach — an administrative act rather than a content one. Airbyte reaches the
/// same conclusion by separating its source-editor role from destination-editor.
/// </para>
/// <para>
/// Connections are deliberately absent entirely. They are the credential boundary and are
/// configured out of band; see
/// <c>docs/research/programmatic-source-configuration-safety-2026-08-17.md</c>.
/// </para>
/// </summary>
public static class SourcesEndpoints
{
    public static IEndpointRouteBuilder MapSourcesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sources").WithTags("Sources")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // GET /api/sources - List sources (paginated)
        group.MapGet("/", async (
            [FromQuery] int? skip,
            [FromQuery] int? take,
            [FromServices] ListSources list,
            HttpContext http,
            CancellationToken ct) =>
        {
            var listed = await list.RunAsync(Rest(http), new ListSourcesQuery(skip ?? 0, take ?? 50), ct);
            if (!listed.Succeeded)
                return OperationProblems.Result(listed);

            var page = listed.Result!;
            var items = page.Items.Select(s => SourceResponse.From(s, page.Diagnostics)).ToList();
            return Results.Ok(new PagedResponse<SourceResponse>(items, items.Count, page.HasMore));
        })
        .Produces<PagedResponse<SourceResponse>>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .WithName("ListSources")
        .WithDescription("List sources with pagination (?skip=0&take=50). Returns sync state, never a file listing.")
        .RequireAuthorization("RequireViewer");

        // GET /api/sources/{sourceId}
        group.MapGet("/{sourceId:guid}", async (
            Guid sourceId,
            [FromServices] GetSource get,
            HttpContext http,
            CancellationToken ct) =>
        {
            var read = await get.RunAsync(Rest(http), sourceId, ct);
            return read.Succeeded
                ? Results.Ok(SourceResponse.From(read.Result!.Source, read.Result.Diagnostics))
                : OperationProblems.Result(read);
        })
        .Produces<SourceResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("GetSource")
        .WithDescription("Get a source's configuration summary and sync state.")
        .RequireAuthorization("RequireViewer");

        // POST /api/sources
        group.MapPost("/", async (
            [FromBody] CreateSourceApiRequest request,
            [FromServices] CreateSource create,
            HttpContext http,
            CancellationToken ct) =>
        {
            var created = await create.RunAsync(Rest(http), new NewSource(
                request.Name, request.ConnectionId, request.ScopeJson, request.Description,
                request.SyncIntervalSeconds, request.Provider, request.Secret, request.Scope), ct);
            if (!created.Succeeded)
                return OperationProblems.Result(created);

            var source = created.Result!.Source;
            return Results.Created($"/api/sources/{source.Id}",
                SourceResponse.From(source, includeDiagnostics: true) with { Warning = created.Result.Warning });
        })
        .Produces<SourceResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status502BadGateway)
        .WithName("CreateSource")
        .WithDescription("Create a source inside an existing connection. Administrator only — a source chooses what external data is indexed.")
        .RequireAuthorization("RequireAdmin");

        // PATCH /api/sources/{sourceId}
        group.MapPatch("/{sourceId:guid}", async (
            Guid sourceId,
            [FromBody] JsonElement body,
            [FromServices] UpdateSource update,
            [FromServices] Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (body.ValueKind != JsonValueKind.Object)
                return ApiProblem.BadRequest("The body must be a JSON object.");
            UpdateSourceRequest request;
            try
            {
                request = body.Deserialize<UpdateSourceRequest>(jsonOptions.Value.SerializerOptions) ?? new UpdateSourceRequest();
            }
            // A scope with no type is NotSupportedException rather than JsonException; both are the caller's mistake.
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                return ApiProblem.BadRequest($"Invalid request: {ex.Message}");
            }

            var updated = await update.RunAsync(Rest(http), sourceId, request, ct);
            return updated.Succeeded
                ? Results.Ok(SourceResponse.From(updated.Result!, includeDiagnostics: true))
                : OperationProblems.Result(updated);
        })
        .Accepts<UpdateSourceRequest>("application/json")
        .Produces<SourceResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithName("UpdateSource")
        .WithDescription("Update a source's name, description, scope, sync interval, or enabled state.")
        .RequireAuthorization("RequireAdmin");

        // DELETE /api/sources/{sourceId}
        group.MapDelete("/{sourceId:guid}", async (
            Guid sourceId,
            [FromQuery] string? confirmName,
            [FromServices] DeleteSource delete,
            HttpContext http,
            CancellationToken ct) =>
        {
            var deleted = await delete.RunAsync(Rest(http), sourceId, confirmName, ct);
            return deleted.Succeeded ? Results.NoContent() : OperationProblems.Result(deleted);
        })
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("DeleteSource")
        .WithDescription("Delete a source and its indexed documents. confirmName must be the source's name. The external data itself is untouched.")
        .RequireAuthorization("RequireAdmin");

        // POST /api/sources/{sourceId}/sync
        group.MapPost("/{sourceId:guid}/sync", async (
            Guid sourceId,
            [FromServices] SyncSource sync,
            HttpContext http,
            [FromQuery] bool applyWithheldDeletions = false,
            CancellationToken ct = default) =>
        {
            var synced = await sync.RunAsync(Rest(http), sourceId, applyWithheldDeletions, ct);
            if (!synced.Succeeded)
                return OperationProblems.Result(synced);

            var result = synced.Result!.Result;
            return Results.Ok(new SourceSyncResponse(
                result.Upserted,
                result.Deleted,
                result.UsedDeltaPath,
                result.RequiredResync,
                result.Error,
                result.WithheldDeletions));
        })
        .Produces<SourceSyncResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithName("SyncSource")
        .WithDescription("Run one sync cycle for a source immediately, rather than waiting for the next scheduled poll.")
        .RequireAuthorization("RequireAdmin");

        group.MapPost("/{sourceId:guid}/retry-failed", async (
            Guid sourceId,
            [FromServices] RetryFailedDocuments retryFailed,
            HttpContext http,
            CancellationToken ct) =>
        {
            var retried = await retryFailed.RunAsync(Rest(http), sourceId, ct);
            return retried.Succeeded
                ? Results.Ok(new RetryFailedResponse(retried.Result))
                : OperationProblems.Result(retried);
        })
        .Produces<RetryFailedResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("RetryFailedSourceDocuments")
        .WithDescription("Re-enqueue every document of a source whose ingestion failed, with a fresh attempt budget, rather than waiting for the retry cooldown or a change to the file.")
        .RequireAuthorization("RequireAdmin");

        // GET /api/v1/sources/scope-schema/{provider}
        app.MapGroup("/api/v1/sources").WithTags("Sources")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .MapGet("/scope-schema/{provider}", async (
            string provider,
            [FromServices] GetScopeSchema schema,
            HttpContext http,
            CancellationToken ct) =>
        {
            var read = await schema.RunAsync(Rest(http), provider, ct);
            return read.Succeeded ? Results.Ok(read.Result) : OperationProblems.Result(read);
        })
        .Produces<ScopeSchema>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("GetSourceScopeSchema")
        .WithDescription("The JSON schema of each kind of scope a provider's sources take: the \"scope\" a create or update sends, with its \"type\".")
        .RequireAuthorization("RequireViewer");

        return app;
    }

    /// <summary>A custom source's requested scope: its scopeId, and the baseUrl if one was named.</summary>
    internal static (string? ScopeId, string? BaseUrl) CustomScopeRequest(string? scopeJson)
    {
        if (string.IsNullOrWhiteSpace(scopeJson)) return (null, null);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(scopeJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null);
            static string? Text(JsonElement root, string name) =>
                root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } text ? text : null;
            return (Text(doc.RootElement, "scopeId"), Text(doc.RootElement, "baseUrl"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>Whether an update changes a GitHub source's private flag or repository id.</summary>
    internal static bool GitHubIdentityChanged(string before, string after)
    {
        static (bool Private, long? RepoId, bool IsGitHub) Read(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                bool isPrivate = root.TryGetProperty("private", out var p) && p.ValueKind == JsonValueKind.True;
                long? repoId = root.TryGetProperty("repoId", out var r) && r.TryGetInt64(out long id) ? id : null;
                bool isGitHub = root.TryGetProperty("owner", out _) && root.TryGetProperty("repo", out _);
                return (isPrivate, repoId, isGitHub);
            }
            catch (JsonException)
            {
                return (false, null, false);
            }
        }

        var old = Read(before);
        if (!old.IsGitHub) return false;
        var now = Read(after);
        return old.Private != now.Private || old.RepoId != now.RepoId;
    }

    private static OperationContext Rest(HttpContext http) => new(http.User, OperationSurface.Rest);
}

/// <summary>One sync cycle's outcome, as POST /api/sources/{sourceId}/sync returns it.</summary>
public record SourceSyncResponse(
    int Upserted,
    int Deleted,
    bool UsedDeltaPath,
    bool RequiredResync,
    string? Error,
    int WithheldDeletions);

/// <summary>
/// Request body for creating a source. Separate from <see cref="CreateSourceRequest"/> so the
/// wire contract can carry an optional scope while the store's contract requires one.
/// </summary>
public record CreateSourceApiRequest(
    string? Name,
    Guid? ConnectionId,
    string? ScopeJson = null,
    string? Description = null,
    int? SyncIntervalSeconds = null,
    ConnectionProvider? Provider = null,
    string? Secret = null,
    SourceScope? Scope = null)
{
    // The generated ToString would print the secret into any log line or message it reached.
    protected virtual bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Name = {Name}, ConnectionId = {ConnectionId}, ScopeJson = {ScopeJson}, Scope = {Scope}, Description = {Description}, ")
            .Append($"SyncIntervalSeconds = {SyncIntervalSeconds}, Provider = {Provider}, Secret = {(Secret is null ? "null" : "***")}");
        return true;
    }
}
