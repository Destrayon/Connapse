using System.Security.Claims;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Connectors;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

public static class DocumentsEndpoints
{
    public static IEndpointRouteBuilder MapDocumentsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/containers/{containerId:guid}/files").WithTags("Files");

        // POST /api/containers/{containerId}/files - Upload file(s) to container
        group.MapPost("/", async (
            HttpContext httpContext,
            Guid containerId,
            [FromForm] IFormFileCollection files,
            [FromForm] string? path,
            [FromForm] ChunkingStrategy? strategy,
            [FromServices] IContainerStore containerStore,
            [FromServices] IConnectorFactory connectorFactory,
            [FromServices] IIngestionQueue queue,
            [FromServices] IAuditLogger auditLogger,
            [FromServices] ICloudScopeService cloudScopeService,
            CancellationToken ct) =>
        {
            var container = await containerStore.GetAsync(containerId, ct);
            if (container is null)
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            // Cloud scope enforcement
            var scopeDenied = await EnforceCloudScope(httpContext, container, cloudScopeService, ct);
            if (scopeDenied is not null) return scopeDenied;

            if (IsReadOnlyConnector(container.ConnectorType))
                return ReadOnlyResult(container);

            if (files.Count == 0)
                return Results.BadRequest(new { error = "No files provided" });

            // Reject path traversal attempts before normalization
            if (path is not null && PathUtilities.ContainsPathTraversal(path))
                return Results.BadRequest(new { error = "Path must not contain '..' segments" });

            var destinationPath = PathUtilities.NormalizeFolderPath(path ?? "/");
            var uploadedDocs = new List<UploadedDocumentResponse>();
            string? batchId = files.Count > 1 ? Guid.NewGuid().ToString() : null;

            foreach (var file in files)
            {
                var documentId = Guid.NewGuid().ToString();
                // Virtual path used for display and returned in response
                var virtualFilePath = PathUtilities.NormalizePath($"{destinationPath}{file.FileName}");

                try
                {
                    // Write files through the connector so each container's storage is
                    // properly scoped (e.g. MinIO prefix, Filesystem rootPath).
                    string jobPath;
                    var connector = connectorFactory.Create(container);
                    var relativePath = virtualFilePath.TrimStart('/');
                    using var stream = file.OpenReadStream();
                    await connector.WriteFileAsync(relativePath, stream, file.ContentType, ct);

                    if (container.ConnectorType == ConnectorType.Filesystem)
                    {
                        var fsConnector = (FilesystemConnector)connector;
                        jobPath = Path.Combine(fsConnector.RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    }
                    else
                    {
                        jobPath = virtualFilePath;
                    }

                    // Enqueue ingestion job
                    var job = new IngestionJob(
                        JobId: Guid.NewGuid().ToString(),
                        DocumentId: documentId,
                        Path: jobPath,
                        Options: new IngestionOptions(
                            DocumentId: documentId,
                            FileName: file.FileName,
                            ContentType: file.ContentType,
                            ContainerId: containerId.ToString(),
                            Path: virtualFilePath,
                            Strategy: strategy ?? ChunkingStrategy.Semantic,
                            Metadata: new Dictionary<string, string>
                            {
                                ["OriginalFileName"] = file.FileName,
                                ["UploadedAt"] = DateTime.UtcNow.ToString("O")
                            }),
                        BatchId: batchId);

                    await queue.EnqueueAsync(job, ct);

                    uploadedDocs.Add(new UploadedDocumentResponse(
                        DocumentId: documentId,
                        JobId: job.JobId,
                        FileName: file.FileName,
                        SizeBytes: file.Length,
                        Path: virtualFilePath)); // return virtual path to the UI
                }
                catch (Exception ex)
                {
                    uploadedDocs.Add(new UploadedDocumentResponse(
                        DocumentId: documentId,
                        JobId: null,
                        FileName: file.FileName,
                        SizeBytes: file.Length,
                        Path: virtualFilePath,
                        Error: ex.Message));
                }
            }

            var successCount = uploadedDocs.Count(d => d.Error == null);
            if (successCount > 0)
                await auditLogger.LogAsync("doc.uploaded", "container", containerId.ToString(),
                    new { FileCount = successCount, ContainerId = containerId }, ct);

            return Results.Ok(new UploadResponse(
                BatchId: batchId,
                Documents: uploadedDocs,
                TotalCount: files.Count,
                SuccessCount: successCount));
        })
        .DisableAntiforgery()
        .WithName("UploadFiles")
        .WithDescription("Upload one or more files to a container")
        .RequireAuthorization("RequireEditor");

        // GET /api/containers/{containerId}/files - List files and folders at path (paginated)
        group.MapGet("/", async (
            HttpContext httpContext,
            Guid containerId,
            [FromQuery] string? path,
            [FromQuery] int skip,
            [FromQuery] int take,
            [FromServices] IContainerStore containerStore,
            [FromServices] IDocumentStore documentStore,
            [FromServices] IFolderStore folderStore,
            [FromServices] ICloudScopeService cloudScopeService,
            CancellationToken ct) =>
        {
            if (skip < 0) skip = 0;
            if (take <= 0 || take > 200) take = 50;

            var container = await containerStore.GetAsync(containerId, ct);
            if (container is null)
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            // Cloud scope enforcement
            var scopeResult = await ResolveCloudScope(httpContext, container, cloudScopeService, ct);
            if (scopeResult is { HasAccess: false })
                return CloudAccessDenied(scopeResult, containerId);

            var browsePath = PathUtilities.NormalizeFolderPath(path ?? "/");

            // If scoped to specific prefixes, block browsing outside allowed areas
            if (scopeResult is not null && !scopeResult.IsPathAllowed(browsePath))
                return Results.Json(new
                {
                    error = "cloud_scope_violation",
                    message = "You do not have access to this path.",
                    allowedPrefixes = scopeResult.AllowedPrefixes
                }, statusCode: 403);

            var entries = new List<BrowseEntry>();

            // Get explicit folders at this level (load all — scope filtering is in-memory)
            var folders = await folderStore.ListAsync(containerId, parentPath: browsePath, take: int.MaxValue, ct: ct);
            foreach (var folder in folders)
            {
                // Filter out folders outside allowed prefixes
                if (scopeResult is not null && !scopeResult.IsPathAllowed(folder.Path))
                    continue;

                var folderName = PathUtilities.GetFileName(folder.Path.TrimEnd('/'));
                entries.Add(new BrowseEntry(
                    Name: folderName,
                    Path: folder.Path,
                    IsFolder: true,
                    SizeBytes: null,
                    LastModified: folder.CreatedAt,
                    Status: null,
                    Id: folder.Id));
            }

            // Get documents at this path level (load all — parent-path and scope filtering is in-memory)
            var documents = await documentStore.ListAsync(containerId, pathPrefix: browsePath, take: int.MaxValue, ct: ct);
            foreach (var doc in documents)
            {
                // Only include documents directly at this level (not in subfolders)
                var docParent = PathUtilities.GetParentPath(doc.Path);
                if (!string.Equals(docParent, browsePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Filter out documents outside allowed prefixes
                if (scopeResult is not null && !scopeResult.IsPathAllowed(doc.Path))
                    continue;

                entries.Add(new BrowseEntry(
                    Name: doc.FileName,
                    Path: doc.Path,
                    IsFolder: false,
                    SizeBytes: doc.SizeBytes,
                    LastModified: doc.CreatedAt,
                    Status: doc.Metadata.GetValueOrDefault("Status"),
                    Id: doc.Id));
            }

            // Sort: folders first, then by name
            entries.Sort((a, b) =>
            {
                if (a.IsFolder != b.IsFolder)
                    return a.IsFolder ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            // Paginate the combined, sorted result
            var totalCount = entries.Count;
            var paged = entries.Skip(skip).Take(take).ToList();
            var hasMore = skip + take < totalCount;

            return Results.Ok(new PagedResponse<BrowseEntry>(paged, totalCount, hasMore));
        })
        .WithName("ListFiles")
        .WithDescription("List files and folders at a path within a container (?skip=0&take=50)")
        .RequireAuthorization("RequireViewer");

        // GET /api/containers/{containerId}/files/{fileId} - Get file details
        group.MapGet("/{fileId}", async (
            HttpContext httpContext,
            Guid containerId,
            string fileId,
            [FromServices] IContainerStore containerStore,
            [FromServices] IDocumentStore documentStore,
            [FromServices] ICloudScopeService cloudScopeService,
            CancellationToken ct) =>
        {
            var container = await containerStore.GetAsync(containerId, ct);
            if (container is null)
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            // Cloud scope enforcement
            var scopeDenied = await EnforceCloudScope(httpContext, container, cloudScopeService, ct);
            if (scopeDenied is not null) return scopeDenied;

            var document = await documentStore.GetAsync(fileId, ct);
            if (document is null || document.ContainerId != containerId.ToString())
                return Results.NotFound(new { error = $"File {fileId} not found in container {containerId}" });

            return Results.Ok(document);
        })
        .WithName("GetFile")
        .WithDescription("Get file details including indexing status")
        .RequireAuthorization("RequireViewer");

        // DELETE /api/containers/{containerId}/files/{fileId} - Delete file
        group.MapDelete("/{fileId}", async (
            HttpContext httpContext,
            Guid containerId,
            string fileId,
            [FromServices] IContainerStore containerStore,
            [FromServices] IDocumentStore documentStore,
            [FromServices] IConnectorFactory connectorFactory,
            [FromServices] IIngestionQueue ingestionQueue,
            [FromServices] IAuditLogger auditLogger,
            [FromServices] ICloudScopeService cloudScopeService,
            CancellationToken ct) =>
        {
            var container = await containerStore.GetAsync(containerId, ct);
            if (container is null)
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            // Cloud scope enforcement
            var scopeDenied = await EnforceCloudScope(httpContext, container, cloudScopeService, ct);
            if (scopeDenied is not null) return scopeDenied;

            if (IsReadOnlyConnector(container.ConnectorType))
                return ReadOnlyResult(container);

            var document = await documentStore.GetAsync(fileId, ct);
            if (document is null || document.ContainerId != containerId.ToString())
                return Results.NotFound(new { error = $"File {fileId} not found in container {containerId}" });

            // Cancel any in-flight ingestion job for this document
            await ingestionQueue.CancelJobForDocumentAsync(fileId);

            // Delete from database (cascades to chunks and vectors)
            await documentStore.DeleteAsync(fileId, ct);

            // Delete file from storage (best effort) — use the connector so
            // the correct prefix / root path is applied.
            try
            {
                if (!string.IsNullOrEmpty(document.Path))
                {
                    var connector = connectorFactory.Create(container);
                    await connector.DeleteFileAsync(document.Path.TrimStart('/'), ct);
                }
            }
            catch { /* File already deleted or not found */ }

            await auditLogger.LogAsync("doc.deleted", "document", fileId,
                new { FileName = document.FileName, ContainerId = containerId }, ct);

            return Results.NoContent();
        })
        .WithName("DeleteFile")
        .WithDescription("Delete a file and all associated chunks and vectors")
        .RequireAuthorization("RequireEditor");

        // GET /api/containers/{containerId}/files/{fileId}/reindex-check - Check if file needs reindexing
        group.MapGet("/{fileId}/reindex-check", async (
            Guid containerId,
            string fileId,
            [FromServices] IContainerStore containerStore,
            [FromServices] IReindexService reindexService,
            CancellationToken ct) =>
        {
            if (!await containerStore.ExistsAsync(containerId, ct))
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            var check = await reindexService.CheckDocumentAsync(fileId, ct);
            return Results.Ok(new
            {
                documentId = check.DocumentId,
                needsReindex = check.NeedsReindex,
                reason = check.Reason.ToString(),
                currentHash = check.CurrentHash,
                storedHash = check.StoredHash,
                currentChunkingStrategy = check.CurrentChunkingStrategy,
                storedChunkingStrategy = check.StoredChunkingStrategy,
                currentEmbeddingModel = check.CurrentEmbeddingModel,
                storedEmbeddingModel = check.StoredEmbeddingModel
            });
        })
        .WithName("CheckFileReindex")
        .WithDescription("Check if a specific file needs reindexing and why")
        .RequireAuthorization("RequireViewer");

        return app;
    }

    /// <summary>
    /// Resolves cloud scope for the current user. Returns null for non-cloud containers.
    /// </summary>
    private static async Task<CloudScopeResult?> ResolveCloudScope(
        HttpContext httpContext, Container container,
        ICloudScopeService cloudScopeService, CancellationToken ct)
    {
        var userId = GetUserId(httpContext);
        if (userId is null) return null;
        return await cloudScopeService.GetScopesAsync(userId.Value, container, ct);
    }

    /// <summary>
    /// Enforces cloud scope: returns a 403 IResult if access is denied, null if allowed or not a cloud container.
    /// </summary>
    private static async Task<IResult?> EnforceCloudScope(
        HttpContext httpContext, Container container,
        ICloudScopeService cloudScopeService, CancellationToken ct)
    {
        var scopeResult = await ResolveCloudScope(httpContext, container, cloudScopeService, ct);
        if (scopeResult is { HasAccess: false })
            return CloudAccessDenied(scopeResult, Guid.Parse(container.Id));
        return null;
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

// Response DTOs
public record UploadResponse(
    string? BatchId,
    List<UploadedDocumentResponse> Documents,
    int TotalCount,
    int SuccessCount);

public record UploadedDocumentResponse(
    string DocumentId,
    string? JobId,
    string FileName,
    long SizeBytes,
    string Path,
    string? Error = null);

public record BrowseEntry(
    string Name,
    string Path,
    bool IsFolder,
    long? SizeBytes,
    DateTime? LastModified,
    string? Status,
    string? Id);
