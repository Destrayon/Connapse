using System.Security.Claims;
using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
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
            [FromServices] IUploadService uploadService,
            CancellationToken ct) =>
        {
            if (files.Count == 0)
                return Results.BadRequest(new { error = "No files provided" });

            foreach (var f in files)
            {
                if (f.FileName.Length > ValidationConstants.MaxFileNameLength)
                    return Results.BadRequest(new { error = "filename_too_long", message = $"Filename '{f.FileName[..50]}...' exceeds {ValidationConstants.MaxFileNameLength} characters." });
            }

            var userId = GetUserId(httpContext);

            if (files.Count == 1)
            {
                var file = files[0];
                using var stream = file.OpenReadStream();
                var request = new UploadRequest(
                    containerId, file.FileName, stream, userId, path,
                    file.ContentType, strategy?.ToString(), "API");

                var result = await uploadService.UploadAsync(request, ct);
                if (!result.Success)
                    return MapUploadError(result.Error!);

                return Results.Ok(new UploadResponse(
                    BatchId: null,
                    Documents: [new UploadedDocumentResponse(
                        result.DocumentId!, result.JobId!, file.FileName, file.Length,
                        PathUtilities.NormalizePath(
                            PathUtilities.NormalizeFolderPath(path ?? "/") + file.FileName))],
                    TotalCount: 1,
                    SuccessCount: 1));
            }

            // Multi-file: pre-validate all extensions before starting uploads
            // (reject the entire batch if any file is unsupported)
            var fileTypeValidator = httpContext.RequestServices.GetRequiredService<IFileTypeValidator>();
            foreach (var file in files)
            {
                if (!fileTypeValidator.IsSupported(file.FileName))
                {
                    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                    var supported = string.Join(", ", fileTypeValidator.SupportedExtensions.OrderBy(e => e));
                    return Results.BadRequest(new { error = "unsupported_file_type", message = $"File type '{ext}' is not supported. Supported types: {supported}" });
                }
            }

            var uploadRequests = new List<UploadRequest>();
            var streams = new List<Stream>();
            foreach (var file in files)
            {
                var stream = file.OpenReadStream();
                streams.Add(stream);
                uploadRequests.Add(new UploadRequest(
                    containerId, file.FileName, stream, userId, path,
                    file.ContentType, strategy?.ToString(), "API"));
            }

            try
            {
                var bulkRequest = new BulkUploadRequest(containerId, uploadRequests);
                var bulkResult = await uploadService.BulkUploadAsync(bulkRequest, ct);

                var docs = new List<UploadedDocumentResponse>();
                for (var i = 0; i < bulkResult.Results.Count; i++)
                {
                    var r = bulkResult.Results[i];
                    var f = files[i];
                    docs.Add(new UploadedDocumentResponse(
                        r.DocumentId ?? Guid.Empty.ToString(),
                        r.JobId,
                        f.FileName,
                        f.Length,
                        PathUtilities.NormalizePath(
                            PathUtilities.NormalizeFolderPath(path ?? "/") + f.FileName),
                        r.Success ? null : r.Error));
                }

                return Results.Ok(new UploadResponse(
                    BatchId: bulkResult.BatchId,
                    Documents: docs,
                    TotalCount: files.Count,
                    SuccessCount: bulkResult.SuccessCount));
            }
            finally
            {
                foreach (var s in streams) s.Dispose();
            }
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
            [FromQuery] int? skip,
            [FromQuery] int? take,
            [FromServices] IContainerStore containerStore,
            [FromServices] IDocumentStore documentStore,
            [FromServices] IFolderStore folderStore,
            [FromServices] ICloudScopeService cloudScopeService,
            CancellationToken ct) =>
        {
            var effectiveSkip = skip ?? 0;
            var effectiveTake = take ?? 50;
            var validationError = PaginationValidator.Validate(effectiveSkip, effectiveTake);
            if (validationError is not null) return validationError;

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
            var paged = entries.Skip(effectiveSkip).Take(effectiveTake).ToList();
            var hasMore = effectiveSkip + effectiveTake < totalCount;

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
            [FromServices] IFolderStore folderStore,
            [FromServices] IKnowledgeFileSystem fileSystem,
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

            var deleteError = ContainerWriteGuard.CheckWrite(container, WriteOperation.Delete);
            if (deleteError is not null)
                return Results.BadRequest(new { error = "write_denied", message = deleteError });

            var document = await documentStore.GetAsync(fileId, ct);
            if (document is null || document.ContainerId != containerId.ToString())
                return Results.NotFound(new { error = $"File {fileId} not found in container {containerId}" });

            // Cancel any in-flight ingestion job for this document
            await ingestionQueue.CancelJobForDocumentAsync(fileId);

            // Delete from database (cascades to chunks and vectors)
            await documentStore.DeleteAsync(fileId, ct);

            // Clean up empty parent folders
            if (!string.IsNullOrEmpty(document.Path))
                await folderStore.DeleteEmptyAncestorsAsync(containerId, document.Path, ct);

            // Delete file from storage (best effort)
            try
            {
                if (!string.IsNullOrEmpty(document.Path))
                    await fileSystem.DeleteAsync(document.Path, ct);
            }
            catch { /* File already deleted or not found */ }

            await auditLogger.LogAsync("doc.deleted", "document", fileId,
                new { FileName = document.FileName, ContainerId = containerId }, ct);

            return Results.NoContent();
        })
        .WithName("DeleteFile")
        .WithDescription("Delete a file and all associated chunks and vectors")
        .RequireAuthorization("RequireEditor");

        // GET /api/containers/{containerId}/files/{fileId}/content - Get file text content
        group.MapGet("/{fileId}/content", async (
            HttpContext httpContext,
            Guid containerId,
            string fileId,
            [FromServices] IContainerStore containerStore,
            [FromServices] IDocumentStore documentStore,
            [FromServices] IConnectorFactory connectorFactory,
            [FromServices] IEnumerable<IDocumentParser> parsers,
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

            document.Metadata.TryGetValue("Status", out var status);
            if (status is "Pending" or "Processing" or "Queued")
                return Results.BadRequest(new { error = "document_not_ready", message = $"Document is still being ingested (status: {status})" });
            if (status == "Failed")
            {
                document.Metadata.TryGetValue("ErrorMessage", out var errorMsg);
                return Results.BadRequest(new { error = "document_failed", message = $"Document failed ingestion: {errorMsg ?? "unknown error"}" });
            }

            // Read file content from storage
            var connector = connectorFactory.Create(container);
            string content;
            try
            {
                using var rawStream = await connector.ReadFileAsync(document.Path, ct);

                // Buffer non-seekable streams (MinIO, S3, AzureBlob) for parsers
                MemoryStream? buffered = null;
                Stream stream;
                if (!rawStream.CanSeek)
                {
                    buffered = new MemoryStream();
                    await rawStream.CopyToAsync(buffered, ct);
                    buffered.Position = 0;
                    stream = buffered;
                }
                else
                {
                    stream = rawStream;
                }

                try
                {
                    var extension = Path.GetExtension(document.FileName).ToLowerInvariant();
                    if (IsTextExtension(extension))
                    {
                        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                        content = await reader.ReadToEndAsync(ct);
                    }
                    else
                    {
                        var parser = parsers.FirstOrDefault(p => p.SupportedExtensions.Contains(extension));
                        if (parser is null)
                            return Results.BadRequest(new { error = "no_parser", message = $"No parser available for '{extension}' files" });

                        var parsed = await parser.ParseAsync(stream, document.FileName, ct);
                        content = parsed.Content;
                    }
                }
                finally
                {
                    buffered?.Dispose();
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException)
            {
                return Results.NotFound(new { error = "file_not_readable", message = "The backing file could not be read from storage" });
            }
            finally
            {
                (connector as IDisposable)?.Dispose();
            }

            // Content negotiation
            var acceptHeader = httpContext.Request.Headers.Accept.ToString();
            if (acceptHeader.Contains("text/plain"))
            {
                return Results.Text(content, "text/plain");
            }

            return Results.Ok(new
            {
                documentId = document.Id,
                fileName = document.FileName,
                path = document.Path,
                contentType = document.ContentType,
                sizeBytes = document.SizeBytes,
                createdAt = document.CreatedAt,
                content
            });
        })
        .WithName("GetFileContent")
        .WithDescription("Get the full text content of a file. Supports Accept: text/plain for raw text or application/json for structured response.")
        .RequireAuthorization("RequireViewer");

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

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".log",
        ".html", ".htm", ".css", ".js", ".ts", ".jsx", ".tsx",
        ".json", ".xml", ".yaml", ".yml"
    };

    private static bool IsTextExtension(string extension) => TextExtensions.Contains(extension);

    private static IResult MapUploadError(string error)
    {
        if (error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return Results.NotFound(new { error = error });
        if (error.Contains("read-only", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "write_denied", message = error });
        if (error.Contains("access denied", StringComparison.OrdinalIgnoreCase) || error.Contains("cloud identity", StringComparison.OrdinalIgnoreCase))
            return Results.Json(new { error = "cloud_access_denied", message = error }, statusCode: 403);
        if (error.Contains("Unsupported file extension", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "unsupported_file_type", message = error });
        if (error.Contains($"{ValidationConstants.MaxFileNameLength} characters", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "filename_too_long", message = error });
        return Results.BadRequest(new { error = error });
    }

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
