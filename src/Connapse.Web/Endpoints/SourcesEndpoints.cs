using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
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
            [FromServices] ISourceStore sourceStore,
            [FromServices] IAuthorizationService authorization,
            [FromServices] PrivateSourceVisibility visibility,
            HttpContext http,
            CancellationToken ct) =>
        {
            int effectiveSkip = skip ?? 0;
            int effectiveTake = take ?? 50;
            var validationError = PaginationValidator.Validate(effectiveSkip, effectiveTake);
            if (validationError is not null) return validationError;

            var sources = await sourceStore.ListAsync(effectiveSkip, effectiveTake + 1, ct);
            bool hasMore = sources.Count > effectiveTake;
            var page = hasMore ? sources.Take(effectiveTake).ToList() : sources;

            bool diagnostics = await IsAdminAsync(authorization, http);
            // A private GitHub source names a private repository; it is listed only to those who may read it.
            var visible = await visibility.ForAsync(http.User, ct);
            var items = page.Where(visible).Select(s => SourceResponse.From(s, diagnostics)).ToList();

            return Results.Ok(new PagedResponse<SourceResponse>(items, items.Count, hasMore));
        })
        .WithName("ListSources")
        .WithDescription("List sources with pagination (?skip=0&take=50). Returns sync state, never a file listing.")
        .RequireAuthorization("RequireViewer");

        // GET /api/sources/{sourceId}
        group.MapGet("/{sourceId:guid}", async (
            Guid sourceId,
            [FromServices] ISourceStore sourceStore,
            [FromServices] IAuthorizationService authorization,
            [FromServices] PrivateSourceVisibility visibility,
            HttpContext http,
            CancellationToken ct) =>
        {
            var source = await sourceStore.GetAsync(sourceId, ct);
            if (source is null || !(await visibility.ForAsync(http.User, ct))(source))
                return Results.NotFound(new { error = $"Source {sourceId} not found" });

            bool diagnostics = await IsAdminAsync(authorization, http);
            return Results.Ok(SourceResponse.From(source, diagnostics));
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
            [FromServices] ICustomProviderStore customProviders,
            CancellationToken ct) =>
        {
            string? scopeJson = request.ScopeJson;

            // Only a custom source holds a credential of its own — with or without a custom connection;
            // anything else authenticates through its connection, so a secret would be silently dropped.
            bool customSource = request.Provider is ConnectionProvider.Custom;
            if (request.Secret is not null && request.ConnectionId is null && !customSource)
                return Results.BadRequest(new { error = "Only a custom source takes a secret.", field = "secret" });
            string name = request.Name?.Trim() ?? "";

            // The same checks the New source dialog makes, for every custom source: the URL rules, the
            // token, protocol version 1, and a scope the middle man actually offers. On a custom
            // connection the middle man must also be the provider's, so the document IDs it lists are
            // the ones the provider answers for. The URL stored is the normalised one.
            async Task<IResult?> CheckCustomSourceAsync(string? providerBaseUrl)
            {
                CustomSourceScope? scope = CustomSourceScope.TryParse(scopeJson);
                if (scope is null)
                    return Results.BadRequest(new { error = "The scope must name the middle man's baseUrl and scopeId.", field = "scope" });

                CustomSourceProbe probe = await customSources.ProbeAsync(scope.BaseUrl, request.Secret, ct);
                if (probe.Error is not null)
                {
                    return probe.Unreachable
                        ? Results.Json(new { error = probe.Error, field = probe.Field }, statusCode: StatusCodes.Status502BadGateway)
                        : Results.BadRequest(new { error = probe.Error, field = probe.Field });
                }

                if (providerBaseUrl is not null && !string.Equals(probe.BaseUrl, providerBaseUrl, StringComparison.Ordinal))
                    return Results.BadRequest(new
                    {
                        error = $"A custom source on this connection must read its provider's middle man ({providerBaseUrl}).",
                        field = "baseUrl",
                    });

                if (!probe.Scopes.Any(s => s.Id == scope.ScopeId))
                    return Results.BadRequest(new { error = $"The middle man offers no scope '{scope.ScopeId}'.", field = "scopeId" });

                scopeJson = (scope with { BaseUrl = probe.BaseUrl! }).ToJson();
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

                if (request.Secret is not null && connection.Provider != ConnectionProvider.Custom)
                    return Results.BadRequest(new { error = "Only a custom source takes a secret.", field = "secret" });

                if (connection.Provider == ConnectionProvider.Custom)
                {
                    customSource = true;
                    CustomProvider? provider = CustomConnectionConfig.ProviderIdOf(connection.ConfigJson) is { } providerId
                        ? await customProviders.GetAsync(providerId, ct)
                        : null;
                    if (provider is null)
                        return Results.BadRequest(new { error = "This connection's custom provider no longer exists.", field = "connectionId" });
                    if (await CheckCustomSourceAsync(provider.BaseUrl) is { } refusal)
                        return refusal;
                }

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
            else if (request.Provider is ConnectionProvider.Custom)
            {
                if (await CheckCustomSourceAsync(providerBaseUrl: null) is { } refusal)
                    return refusal;
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
                        request.Description, request.SyncIntervalSeconds, request.Provider,
                        customSource ? request.Secret?.Trim() : null), ct);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
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
            [FromBody] UpdateSourceRequest request,
            [FromServices] ISourceStore sourceStore,
            [FromServices] IConnectionStore connectionStore,
            [FromServices] CustomSourceTokenReplacement tokens,
            [FromServices] IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(request.ScopeJson))
            {
                // Disposed: JsonDocument rents pooled buffers, and this runs per request.
                try { using var _ = JsonDocument.Parse(request.ScopeJson); }
                catch (JsonException ex)
                { return Results.BadRequest(new { error = $"Invalid scope JSON: {ex.Message}" }); }
            }

            // A GitHub source's documents are addressed — or deliberately not — by whether it is
            // private, and keyed on its repository id. Changing either in place would leave the
            // documents already indexed with the old addressing until a re-sync replaced them:
            // public rows (no address) of a repository now marked private would stay visible to
            // everyone. So they are fixed at creation; to change them, add the repository again.
            if (!string.IsNullOrWhiteSpace(request.ScopeJson)
                && await sourceStore.GetAsync(sourceId, ct) is { } existing
                && GitHubIdentityChanged(existing.ScopeJson, request.ScopeJson))
            {
                return Results.BadRequest(new
                {
                    error = "A GitHub source's private flag and repository id cannot be changed. Add the repository again as a new source.",
                });
            }

            // A custom source's scope names where its stored token is sent. Changing it in place would
            // let anyone who may edit sources point the token at another server, so it is fixed at
            // creation; to read elsewhere, create a new source and enter the token again.
            if (request.ScopeJson is not null
                && await sourceStore.GetAsync(sourceId, ct) is { } custom
                && (custom.Provider is ConnectionProvider.Custom
                    || (custom.ConnectionId is Guid customConnectionId
                        && await connectionStore.GetAsync(customConnectionId, ct) is { Provider: ConnectionProvider.Custom }))
                && CustomSourceScope.TryParse(request.ScopeJson) != CustomSourceScope.TryParse(custom.ScopeJson))
            {
                return Results.BadRequest(new
                {
                    error = "A custom source's middle man and scope cannot be changed. Create a new source and enter its token again.",
                    field = "scope",
                });
            }

            // A Confluence source keeps its space; what it reads from it is checked as on creation.
            // A Jira source, known by its stored kind, keeps its project the same way.
            if (request.ScopeJson is not null
                && await sourceStore.GetAsync(sourceId, ct) is { ConnectionId: Guid connectionId } current
                && await connectionStore.GetAsync(connectionId, ct) is { Provider: ConnectionProvider.Atlassian })
            {
                var atlassian = AtlassianSourceScope.IsJira(current.ScopeJson)
                    ? JiraSourceScope.Update(current.ScopeJson, request.ScopeJson)
                    : ConfluenceSourceScope.Update(current.ScopeJson, request.ScopeJson);
                if (atlassian.Outcome != AtlassianSourceScope.Outcome.Valid)
                    return Results.BadRequest(new { error = atlassian.Error, field = atlassian.Field });
                request = request with { ScopeJson = atlassian.ScopeJson };
            }

            // A custom source's new token, when its middle man rotated the old one: checked against the source's
            // fixed middle man and scope before it replaces the stored one. It travels alone, so a request either
            // replaces the token or changes the other fields, never half of both.
            if (request.Secret is not null)
            {
                if (request with { Secret = null } != new UpdateSourceRequest())
                    return Results.BadRequest(new { error = "Replace the token in a request of its own, without other changes.", field = "secret" });

                CustomTokenReplacement replaced = await tokens.ReplaceAsync(sourceId, request.Secret, ct);
                if (replaced.NotFound)
                    return Results.NotFound(new { error = replaced.Error });
                if (replaced.Error is not null)
                {
                    return replaced.Unreachable
                        ? Results.Json(new { error = replaced.Error, field = replaced.Field }, statusCode: StatusCodes.Status502BadGateway)
                        : Results.BadRequest(new { error = replaced.Error, field = replaced.Field });
                }

                Source? rotated = await sourceStore.GetAsync(sourceId, ct);
                return rotated is null
                    ? Results.NotFound(new { error = $"Source {sourceId} not found" })
                    : Results.Ok(SourceResponse.From(rotated, includeDiagnostics: true));
            }

            var updated = await sourceStore.UpdateAsync(sourceId, request, ct);
            if (updated is null)
                return Results.NotFound(new { error = $"Source {sourceId} not found" });

            await auditLogger.LogAsync("source.updated", "source", sourceId.ToString(),
                new { updated.Name }, ct);

            return Results.Ok(SourceResponse.From(updated, includeDiagnostics: true));
        })
        .WithName("UpdateSource")
        .WithDescription("Update a source's name, description, scope, sync interval, or enabled state, or replace a custom source's token (secret).")
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

    /// <summary>
    /// Whether the caller may see diagnostic detail. Reads are open to viewers, but a
    /// provider's failure text tends to quote the resource that failed, so it is withheld
    /// from anyone who is not already entitled to know the source's scope.
    /// </summary>
    private static async Task<bool> IsAdminAsync(IAuthorizationService authorization, HttpContext http) =>
        (await authorization.AuthorizeAsync(http.User, "RequireAdmin")).Succeeded;
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
