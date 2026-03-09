using System.Security.Claims;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

public static class FoldersEndpoints
{
    public static IEndpointRouteBuilder MapFoldersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/containers/{containerId:guid}/folders").WithTags("Folders")
            .RequireAuthorization("RequireEditor");

        // POST /api/containers/{containerId}/folders - Create folder
        group.MapPost("/", async (
            HttpContext httpContext,
            Guid containerId,
            [FromBody] CreateFolderRequest request,
            [FromServices] IContainerStore containerStore,
            [FromServices] IFolderStore folderStore,
            [FromServices] ICloudScopeService cloudScopeService,
            CancellationToken ct) =>
        {
            var container = await containerStore.GetAsync(containerId, ct);
            if (container is null)
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            // Cloud scope enforcement
            var scopeResult = await ResolveCloudScope(httpContext, container, cloudScopeService, ct);
            if (scopeResult is { HasAccess: false })
                return CloudAccessDenied(scopeResult, containerId);

            if (IsReadOnlyConnector(container.ConnectorType))
                return ReadOnlyResult(container);

            if (string.IsNullOrWhiteSpace(request.Path))
                return Results.BadRequest(new { error = "Folder path is required" });

            // Reject path traversal attempts
            if (PathUtilities.ContainsPathTraversal(request.Path))
                return Results.BadRequest(new { error = "Path must not contain '..' segments" });

            var normalizedPath = PathUtilities.NormalizeFolderPath(request.Path);

            // Verify path is within allowed prefixes
            if (scopeResult is not null && !scopeResult.IsPathAllowed(normalizedPath))
                return Results.Json(new
                {
                    error = "cloud_scope_violation",
                    message = "You do not have access to create folders at this path.",
                    allowedPrefixes = scopeResult.AllowedPrefixes
                }, statusCode: 403);

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
            HttpContext httpContext,
            Guid containerId,
            [FromQuery] string path,
            [FromQuery] bool cascade,
            [FromServices] IContainerStore containerStore,
            [FromServices] IFolderStore folderStore,
            [FromServices] IDocumentStore documentStore,
            [FromServices] IConnectorFactory connectorFactory,
            [FromServices] IIngestionQueue ingestionQueue,
            [FromServices] ICloudScopeService cloudScopeService,
            CancellationToken ct) =>
        {
            var container = await containerStore.GetAsync(containerId, ct);
            if (container is null)
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            // Cloud scope enforcement
            var scopeResult = await ResolveCloudScope(httpContext, container, cloudScopeService, ct);
            if (scopeResult is { HasAccess: false })
                return CloudAccessDenied(scopeResult, containerId);

            if (IsReadOnlyConnector(container.ConnectorType))
                return ReadOnlyResult(container);

            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "Folder path is required" });

            var normalizedPath = PathUtilities.NormalizeFolderPath(path);

            // Verify path is within allowed prefixes
            if (scopeResult is not null && !scopeResult.IsPathAllowed(normalizedPath))
                return Results.Json(new
                {
                    error = "cloud_scope_violation",
                    message = "You do not have access to delete folders at this path.",
                    allowedPrefixes = scopeResult.AllowedPrefixes
                }, statusCode: 403);

            if (!await folderStore.ExistsAsync(containerId, normalizedPath, ct))
                return Results.NotFound(new { error = $"Folder '{normalizedPath}' not found" });

            // Get documents before deletion so we can cancel jobs and clean up storage
            var documents = await documentStore.ListAsync(containerId, pathPrefix: normalizedPath, take: int.MaxValue, ct: ct);
            var filePaths = documents.Select(d => d.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();

            // Cancel any in-flight ingestion jobs for documents in this folder
            foreach (var doc in documents)
                await ingestionQueue.CancelJobForDocumentAsync(doc.Id);

            var deleted = await folderStore.DeleteAsync(containerId, normalizedPath, ct);
            if (!deleted)
                return Results.BadRequest(new { error = "Folder is not empty. Use cascade=true to delete contents." });

            // Clean up file storage (best effort) — use the connector so
            // the correct prefix / root path is applied.
            var connector = connectorFactory.Create(container);
            foreach (var filePath in filePaths)
            {
                try { await connector.DeleteFileAsync(filePath.TrimStart('/'), ct); }
                catch { /* File already deleted or not found */ }
            }

            return Results.NoContent();
        })
        .WithName("DeleteFolder")
        .WithDescription("Delete a folder, optionally cascading to nested files and subfolders");

        return app;
    }

    private static async Task<CloudScopeResult?> ResolveCloudScope(
        HttpContext httpContext, Container container,
        ICloudScopeService cloudScopeService, CancellationToken ct)
    {
        var userId = GetUserId(httpContext);
        if (userId is null) return null;
        return await cloudScopeService.GetScopesAsync(userId.Value, container, ct);
    }

    private static IResult CloudAccessDenied(CloudScopeResult scopeResult, Guid containerId) =>
        Results.Json(new
        {
            error = "cloud_access_denied",
            message = scopeResult.Error ?? "Access denied.",
            containerId = containerId.ToString()
        }, statusCode: 403);

    private static bool IsReadOnlyConnector(ConnectorType type) =>
        type is ConnectorType.S3 or ConnectorType.AzureBlob;

    private static IResult ReadOnlyResult(Container container) =>
        Results.BadRequest(new
        {
            error = "read_only_container",
            message = $"{container.ConnectorType} containers are read-only. Files are synced from the source."
        });

    private static Guid? GetUserId(HttpContext httpContext)
    {
        var idClaim = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(idClaim, out var userId) ? userId : null;
    }
}

public record CreateFolderRequest(string Path);
