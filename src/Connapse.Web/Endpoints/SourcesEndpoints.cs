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
        var group = app.MapGroup("/api/sources").WithTags("Sources");

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
        .WithName("GetSource")
        .WithDescription("Get a source's configuration summary and sync state.")
        .RequireAuthorization("RequireViewer");

        // POST /api/sources
        group.MapPost("/", async (
            [FromBody] CreateSourceApiRequest request,
            [FromServices] ISourceStore sourceStore,
            [FromServices] IConnectionStore connectionStore,
            [FromServices] IAuditLogger auditLogger,
            [FromServices] Connapse.Storage.Connectors.GitHub.GitHubRepositoryLookup gitHubRepositories,
            [FromServices] AtlassianSiteService atlassianSites,
            [FromServices] CustomSourceSetup customSources,
            CancellationToken ct) =>
        {
            string? scopeJson = request.ScopeJson;

            // Every source authenticates through its connection, a custom one included, so a secret here
            // would be silently dropped.
            if (request.Secret is not null)
                return Results.BadRequest(new
                {
                    error = "A source takes no secret: its connection holds the credential. A custom source reads with its custom connection's token.",
                    field = "secret",
                });
            if (request.Provider is ConnectionProvider.Custom)
                return Results.BadRequest(new
                {
                    error = "A custom source needs a custom connection: pass its connectionId. An administrator adds one on the Connections page.",
                    field = "connectionId",
                });
            string name = request.Name?.Trim() ?? "";

            // The same checks the New source dialog makes: the connection's middle man answers with its token,
            // speaks protocol version 1, and offers the scope. The scope names only the scopeId; a baseUrl, if
            // given, must be the connection's, and the one stored is the connection's normalised address.
            async Task<IResult?> CheckCustomSourceAsync(Connection connection)
            {
                (string? scopeId, string? baseUrl) = CustomScopeRequest(scopeJson);
                if (scopeId is null)
                    return Results.BadRequest(new { error = "The scope must name the source server's scopeId.", field = "scopeId" });

                CustomSourceProbe probe = await customSources.ProbeConnectionAsync(connection, ct);
                if (probe.Error is not null)
                {
                    return probe.Unreachable
                        ? Results.Json(new { error = probe.Error, field = probe.Field }, statusCode: StatusCodes.Status502BadGateway)
                        : Results.BadRequest(new { error = probe.Error, field = probe.Field });
                }

                if (baseUrl is not null && !string.Equals(baseUrl.TrimEnd('/'), probe.BaseUrl, StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new
                    {
                        error = $"A custom source reads its connection's source server ({probe.BaseUrl}). Leave baseUrl out, or pass that one.",
                        field = "baseUrl",
                    });

                if (!probe.Scopes.Any(s => s.Id == scopeId))
                    return Results.BadRequest(new { error = $"The source server offers no scope '{scopeId}'.", field = "scopeId" });

                scopeJson = new CustomSourceScope(probe.BaseUrl!, scopeId).ToJson();
                return null;
            }

            // Only a Confluence source may leave its name out: like the dialog, it is then named after the site and space.
            if (name.Length == 0 && request.ConnectionId is null)
                return Results.BadRequest(new { error = "Source name is required", field = "name" });

            if (request.ConnectionId is not null && request.Provider is not null)
            {
                return Results.BadRequest(new { error = "a source must have exactly one of a connectionId or a provider" });
            }

            if (request.ConnectionId is Guid cid)
            {
                // Checked before anything else touches the database: a source whose connection
                // does not exist is skipped silently by SourceSyncService, so it would look
                // created and never sync.
                var connection = await connectionStore.GetAsync(cid, ct);
                if (connection is null)
                    return Results.BadRequest(new { error = $"Connection {cid} not found" });

                if (connection.Provider == ConnectionProvider.Custom
                    && await CheckCustomSourceAsync(connection) is { } refusal)
                    return refusal;

                // The same lookup the New source dialog makes: GitHub, as this installation, says
                // which repository the name is and whether it is private. Its answer replaces
                // whatever the request claimed, so an API-created source matches a UI-created one.
                if (connection.Provider == ConnectionProvider.GitHub)
                {
                    var resolved = await GitHubSourceScope.ResolveAsync(scopeJson, connection, gitHubRepositories, ct);
                    if (resolved.Error is not null)
                        return Results.BadRequest(new { error = resolved.Error });
                    scopeJson = resolved.ScopeJson;
                }

                // The checks the New source dialog runs for a Confluence space or a Jira project
                // (by the scope's kind; none means Confluence), against the site as its service
                // account; the scope stored is rebuilt from Atlassian's answer.
                if (connection.Provider == ConnectionProvider.Atlassian)
                {
                    var atlassian = AtlassianSourceScope.IsJira(scopeJson)
                        ? await JiraSourceScope.ResolveAsync(scopeJson, connection, atlassianSites, sourceStore, ct)
                        : await ConfluenceSourceScope.ResolveAsync(scopeJson, connection, atlassianSites, sourceStore, ct);
                    switch (atlassian.Outcome)
                    {
                        case AtlassianSourceScope.Outcome.Invalid:
                            return Results.BadRequest(new { error = atlassian.Error, field = atlassian.Field });
                        case AtlassianSourceScope.Outcome.Duplicate:
                            return Results.Conflict(new { error = atlassian.Error, field = atlassian.Field });
                        case AtlassianSourceScope.Outcome.Unavailable:
                            return Results.Json(new { error = atlassian.Error, field = atlassian.Field },
                                statusCode: StatusCodes.Status502BadGateway);
                    }

                    scopeJson = atlassian.ScopeJson;
                    if (name.Length == 0)
                        name = atlassian.DefaultName!;
                }

                if (name.Length == 0)
                    return Results.BadRequest(new { error = "Source name is required", field = "name" });
            }
            else if (request.Provider is null)
            {
                return Results.BadRequest(new { error = "a source needs a connectionId or a provider" });
            }
            else if (request.Provider is ConnectionProvider.GitHub)
            {
                // Refused rather than stored: the connector no longer reads GitHub anonymously, so
                // such a source would be created only to fail every sync.
                return Results.BadRequest(new
                {
                    error = "GitHub sources need a GitHub App connection: pass the connectionId of a GitHub installation instead of a provider",
                });
            }

            if (!string.IsNullOrWhiteSpace(scopeJson))
            {
                // Disposed: JsonDocument rents pooled buffers, and this runs per request.
                try { using var _ = JsonDocument.Parse(scopeJson); }
                catch (JsonException ex)
                { return Results.BadRequest(new { error = $"Invalid scope JSON: {ex.Message}" }); }
            }

            var existing = await sourceStore.GetByNameAsync(name, ct);
            if (existing is not null)
                return Results.Conflict(new { error = $"Source '{name}' already exists" });

            Source created;
            try
            {
                created = await sourceStore.CreateAsync(
                    new CreateSourceRequest(
                        name, request.ConnectionId, scopeJson ?? "{}",
                        request.Description, request.SyncIntervalSeconds, request.Provider), ct);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (DuplicateSourceScopeException ex)
            {
                return Results.Conflict(new { error = ex.Message, field = "scopeId" });
            }

            await auditLogger.LogAsync("source.created", "source", created.Id.ToString(),
                new { created.Name, created.ConnectionId }, ct);

            return Results.Created($"/api/sources/{created.Id}", SourceResponse.From(created, includeDiagnostics: true));
        })
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
                return Results.BadRequest(new { error = "The body must be a JSON object." });
            UpdateSourceRequest request;
            try
            {
                request = body.Deserialize<UpdateSourceRequest>(jsonOptions.Value.SerializerOptions) ?? new UpdateSourceRequest();
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = $"Invalid request: {ex.Message}" });
            }

            var updated = await update.RunAsync(Rest(http), sourceId, request, ct);
            return updated.Succeeded
                ? Results.Ok(SourceResponse.From(updated.Result!, includeDiagnostics: true))
                : OperationProblems.Result(updated);
        })
        .Accepts<UpdateSourceRequest>("application/json")
        .WithName("UpdateSource")
        .WithDescription("Update a source's name, description, scope, sync interval, or enabled state.")
        .RequireAuthorization("RequireAdmin");

        // DELETE /api/sources/{sourceId}
        group.MapDelete("/{sourceId:guid}", async (
            Guid sourceId,
            [FromQuery] string? confirmName,
            [FromServices] ISourceStore sourceStore,
            [FromServices] IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            var source = await sourceStore.GetAsync(sourceId, ct);
            if (source is null)
                return Results.NotFound(new { error = $"Source {sourceId} not found" });

            // The name the Sources page asks to be typed, since this removes every indexed document.
            if (!SourceDeletion.Confirmed(source, confirmName))
                return Results.BadRequest(new { error = SourceDeletion.ConfirmMismatch(source), field = "confirmName" });

            bool deleted = await sourceStore.DeleteAsync(sourceId, ct);
            if (!deleted)
                return Results.NotFound(new { error = $"Source {sourceId} not found" });

            await auditLogger.LogAsync("source.deleted", "source", sourceId.ToString(),
                new { source.Name }, ct);

            return Results.NoContent();
        })
        .WithName("DeleteSource")
        .WithDescription("Delete a source and its indexed documents. confirmName must be the source's name. The external data itself is untouched.")
        .RequireAuthorization("RequireAdmin");

        // POST /api/sources/{sourceId}/sync
        group.MapPost("/{sourceId:guid}/sync", async (
            Guid sourceId,
            [FromServices] ISourceStore sourceStore,
            [FromServices] IConnectionStore connectionStore,
            [FromServices] SourceSyncService syncService,
            [FromServices] IAuditLogger auditLogger,
            [FromQuery] bool applyWithheldDeletions = false,
            CancellationToken ct = default) =>
        {
            var source = await sourceStore.GetAsync(sourceId, ct);
            if (source is null)
                return Results.NotFound(new { error = $"Source {sourceId} not found" });

            if (!source.Enabled)
                return Results.BadRequest(new { error = $"Source '{source.Name}' is disabled" });

            // A connection-less source (a GitHub source added before App connections) syncs through its Provider; only a source
            // that names a connection can be missing one.
            Connection? connection = null;
            if (source.ConnectionId is Guid connectionId)
            {
                connection = await connectionStore.GetAsync(connectionId, ct);
                if (connection is null)
                    return Results.BadRequest(new { error = $"Source '{source.Name}' references a missing connection" });
            }
            else if (source.Provider is null)
            {
                return Results.BadRequest(new { error = $"Source '{source.Name}' has neither a connection nor a provider" });
            }

            var result = await syncService.SyncSourceAsync(source, connection, ct, applyWithheldDeletions);

            // Nothing went wrong — the scheduled cycle got there first. 409 rather than 200
            // so a script driving this does not read "0 upserted" as "nothing to do".
            if (result.AlreadyRunning)
            {
                return Results.Conflict(new
                {
                    error = $"A sync for source '{source.Name}' is already in progress.",
                });
            }

            await auditLogger.LogAsync(
                applyWithheldDeletions ? "source.deletions_applied" : "source.synced",
                "source", source.Id.ToString(),
                new { source.Name, result.Upserted, result.Deleted, result.WithheldDeletions }, ct);

            return Results.Ok(new
            {
                result.Upserted,
                result.Deleted,
                result.UsedDeltaPath,
                result.RequiredResync,
                result.Error,
                result.WithheldDeletions
            });
        })
        .WithName("SyncSource")
        .WithDescription("Run one sync cycle for a source immediately, rather than waiting for the next scheduled poll.")
        .RequireAuthorization("RequireAdmin");

        group.MapPost("/{sourceId:guid}/retry-failed", async (
            Guid sourceId,
            [FromServices] ISourceStore sourceStore,
            [FromServices] IReindexService reindexService,
            [FromServices] IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            var source = await sourceStore.GetAsync(sourceId, ct);
            if (source is null)
                return Results.NotFound(new { error = $"Source {sourceId} not found" });

            int enqueued = await reindexService.RetryFailedAsync(source.Id, ct);
            await auditLogger.LogAsync("source.retry_failed", "source", source.Id.ToString(), new { source.Name, enqueued }, ct);

            return Results.Ok(new { enqueuedCount = enqueued });
        })
        .WithName("RetryFailedSourceDocuments")
        .WithDescription("Re-enqueue every document of a source whose ingestion failed, with a fresh attempt budget, rather than waiting for the retry cooldown or a change to the file.")
        .RequireAuthorization("RequireAdmin");

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
    string? Secret = null)
{
    // The generated ToString would print the secret into any log line or message it reached.
    protected virtual bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Name = {Name}, ConnectionId = {ConnectionId}, ScopeJson = {ScopeJson}, Description = {Description}, ")
            .Append($"SyncIntervalSeconds = {SyncIntervalSeconds}, Provider = {Provider}, Secret = {(Secret is null ? "null" : "***")}");
        return true;
    }
}
