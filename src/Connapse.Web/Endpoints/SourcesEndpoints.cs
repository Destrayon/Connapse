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
            var items = page.Select(s => SourceResponse.From(s, diagnostics)).ToList();

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
            HttpContext http,
            CancellationToken ct) =>
        {
            var source = await sourceStore.GetAsync(sourceId, ct);
            if (source is null)
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
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "Source name is required" });

            string name = request.Name.Trim();

            // Checked before anything else touches the database: a source whose connection
            // does not exist is skipped silently by SourceSyncService, so it would look
            // created and never sync.
            var connection = await connectionStore.GetAsync(request.ConnectionId, ct);
            if (connection is null)
                return Results.BadRequest(new { error = $"Connection {request.ConnectionId} not found" });

            if (!string.IsNullOrWhiteSpace(request.ScopeJson))
            {
                try { JsonDocument.Parse(request.ScopeJson); }
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
                        name, request.ConnectionId, request.ScopeJson ?? "{}",
                        request.Description, request.SyncIntervalSeconds), ct);
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
            [FromServices] IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(request.ScopeJson))
            {
                try { JsonDocument.Parse(request.ScopeJson); }
                catch (JsonException ex)
                { return Results.BadRequest(new { error = $"Invalid scope JSON: {ex.Message}" }); }
            }

            var updated = await sourceStore.UpdateAsync(sourceId, request, ct);
            if (updated is null)
                return Results.NotFound(new { error = $"Source {sourceId} not found" });

            await auditLogger.LogAsync("source.updated", "source", sourceId.ToString(),
                new { updated.Name }, ct);

            return Results.Ok(SourceResponse.From(updated, includeDiagnostics: true));
        })
        .WithName("UpdateSource")
        .WithDescription("Update a source's name, description, scope, sync interval, or enabled state.")
        .RequireAuthorization("RequireAdmin");

        // DELETE /api/sources/{sourceId}
        group.MapDelete("/{sourceId:guid}", async (
            Guid sourceId,
            [FromServices] ISourceStore sourceStore,
            [FromServices] IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            var source = await sourceStore.GetAsync(sourceId, ct);
            if (source is null)
                return Results.NotFound(new { error = $"Source {sourceId} not found" });

            bool deleted = await sourceStore.DeleteAsync(sourceId, ct);
            if (!deleted)
                return Results.NotFound(new { error = $"Source {sourceId} not found" });

            await auditLogger.LogAsync("source.deleted", "source", sourceId.ToString(),
                new { source.Name }, ct);

            return Results.NoContent();
        })
        .WithName("DeleteSource")
        .WithDescription("Delete a source and its indexed documents. The external data itself is untouched.")
        .RequireAuthorization("RequireAdmin");

        // POST /api/sources/{sourceId}/sync
        group.MapPost("/{sourceId:guid}/sync", async (
            Guid sourceId,
            [FromServices] ISourceStore sourceStore,
            [FromServices] IConnectionStore connectionStore,
            [FromServices] SourceSyncService syncService,
            [FromServices] IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            var source = await sourceStore.GetAsync(sourceId, ct);
            if (source is null)
                return Results.NotFound(new { error = $"Source {sourceId} not found" });

            if (!source.Enabled)
                return Results.BadRequest(new { error = $"Source '{source.Name}' is disabled" });

            var connection = await connectionStore.GetAsync(source.ConnectionId, ct);
            if (connection is null)
                return Results.BadRequest(new { error = $"Source '{source.Name}' references a missing connection" });

            var result = await syncService.SyncSourceAsync(source, connection, ct);

            await auditLogger.LogAsync("source.synced", "source", sourceId.ToString(),
                new { source.Name, result.Upserted, result.Deleted }, ct);

            return Results.Ok(new
            {
                result.Upserted,
                result.Deleted,
                result.UsedDeltaPath,
                result.RequiredResync,
                result.Error
            });
        })
        .WithName("SyncSource")
        .WithDescription("Run one sync cycle for a source immediately, rather than waiting for the next scheduled poll.")
        .RequireAuthorization("RequireAdmin");

        return app;
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
    string Name,
    Guid ConnectionId,
    string? ScopeJson = null,
    string? Description = null,
    int? SyncIntervalSeconds = null);
