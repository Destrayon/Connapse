using System.Security.Claims;
using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Web.Problems;
using Connapse.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;

namespace Connapse.Web.Endpoints;

public static class DocumentsEndpoints
{
    public static IEndpointRouteBuilder MapDocumentsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/containers/{containerId:guid}/files").WithTags("Files")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

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
                return ApiProblem.BadRequest("No files provided");

            foreach (var f in files)
            {
                if (f.FileName.Length > ValidationConstants.MaxFileNameLength)
                    return ApiProblem.BadRequest("filename_too_long", $"Filename '{f.FileName[..50]}...' exceeds {ValidationConstants.MaxFileNameLength} characters.");
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
                    return ApiProblem.BadRequest("unsupported_file_type", $"File type '{ext}' is not supported. Supported types: {supported}");
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
        .Produces<UploadResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
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
            CancellationToken ct) =>
        {
            var effectiveSkip = skip ?? 0;
            var effectiveTake = take ?? 50;
            var validationError = PaginationValidator.Validate(effectiveSkip, effectiveTake);
            if (validationError is not null) return validationError;

            var container = await containerStore.GetAsync(containerId, ct);
            if (container is null)
                return ApiProblem.NotFound($"Container {containerId} not found");

            var browsePath = PathUtilities.NormalizeFolderPath(path ?? "/");
            var entries = new List<BrowseEntry>();

            // Get explicit folders at this level (load all — scope filtering is in-memory)
            var folders = await folderStore.ListAsync(containerId, parentPath: browsePath, take: int.MaxValue, ct: ct);
            foreach (var folder in folders)
            {
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
        .Produces<PagedResponse<BrowseEntry>>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
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
            [FromServices] DocumentReadGuard readGuard,
            CancellationToken ct) =>
        {
            var container = await containerStore.GetAsync(containerId, ct);
            if (container is null)
                return ApiProblem.NotFound($"Container {containerId} not found");

            var document = await documentStore.GetAsync(fileId, ct);
            if (document is null || document.ContainerId != containerId.ToString()
                || !await readGuard.CanReadAsync(SearchPrincipal.Resolve(httpContext.User), document, ct))
                return ApiProblem.NotFound($"File {fileId} not found in container {containerId}");

            return Results.Ok(document);
        })
        .Produces<Document>()
        .ProducesProblem(StatusCodes.Status404NotFound)
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
            CancellationToken ct) =>
        {
            var container = await containerStore.GetAsync(containerId, ct);
            if (container is null)
                return ApiProblem.NotFound($"Container {containerId} not found");

            var document = await documentStore.GetAsync(fileId, ct);
            if (document is null || document.ContainerId != containerId.ToString())
                return ApiProblem.NotFound($"File {fileId} not found in container {containerId}");

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
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
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
            [FromServices] IManagedStorageProvider managedStorage,
            [FromServices] IEnumerable<IDocumentParser> parsers,
            [FromServices] DocumentReadGuard readGuard,
            CancellationToken ct) =>
        {
            var container = await containerStore.GetAsync(containerId, ct);
            if (container is null)
                return ApiProblem.NotFound($"Container {containerId} not found");

            var document = await documentStore.GetAsync(fileId, ct);
            if (document is null || document.ContainerId != containerId.ToString()
                || !await readGuard.CanReadAsync(SearchPrincipal.Resolve(httpContext.User), document, ct))
                return ApiProblem.NotFound($"File {fileId} not found in container {containerId}");

            document.Metadata.TryGetValue("Status", out var status);
            if (status is "Pending" or "Processing" or "Queued")
                return ApiProblem.BadRequest("document_not_ready", $"Document is still being ingested (status: {status})");
            if (status == "Failed")
            {
                document.Metadata.TryGetValue("ErrorMessage", out var errorMsg);
                return ApiProblem.BadRequest("document_failed", $"Document failed ingestion: {errorMsg ?? "unknown error"}");
            }

            // Read file content from storage
            var connector = managedStorage.CreateConnector(container.Id);
            string content;
            try
            {
                using var rawStream = await connector.ReadFileAsync(document.Path, ct);

                // Buffer non-seekable streams (MinIO, S3) for parsers
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
                            return ApiProblem.BadRequest("no_parser", $"No parser available for '{extension}' files");

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
                return ApiProblem.NotFound("file_not_readable", "The backing file could not be read from storage");
            }
            finally
            {
                (connector as IDisposable)?.Dispose();
            }

            // Content negotiation
            if (PrefersPlainText(httpContext.Request.Headers.Accept.ToString()))
            {
                return Results.Text(content, "text/plain");
            }

            return Results.Ok(new FileContentResponse(
                document.Id,
                document.FileName,
                document.Path,
                document.ContentType,
                document.SizeBytes,
                document.CreatedAt,
                content));
        })
        .Produces<FileContentResponse>()
        // Accept: text/plain gets the text alone. A second Produces for the same status would replace
        // the JSON one, so the plain-text form is added to the document's 200 instead.
        .AddOpenApiOperationTransformer((operation, _, _) =>
        {
            if (operation.Responses?.TryGetValue("200", out var ok) == true && ok.Content is not null)
                ok.Content["text/plain"] = new OpenApiMediaType { Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
            return Task.CompletedTask;
        })
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
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
                return ApiProblem.NotFound($"Container {containerId} not found");

            var check = await reindexService.CheckDocumentAsync(fileId, ct);
            return Results.Ok(new FileReindexCheckResponse(
                check.DocumentId,
                check.NeedsReindex,
                check.Reason.ToString(),
                check.CurrentHash,
                check.StoredHash,
                check.CurrentChunkingStrategy,
                check.StoredChunkingStrategy,
                check.CurrentEmbeddingModel,
                check.StoredEmbeddingModel));
        })
        .Produces<FileReindexCheckResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("CheckFileReindex")
        .WithDescription("Check if a specific file needs reindexing and why")
        .RequireAuthorization("RequireViewer");

        return app;
    }

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".log",
        ".html", ".htm", ".css", ".js", ".ts", ".jsx", ".tsx",
        ".json", ".xml", ".yaml", ".yml"
    };

    private static bool IsTextExtension(string extension) => TextExtensions.Contains(extension);

    /// <summary>Whether a file's content goes back as plain text rather than JSON, for this Accept header.</summary>
    public static bool PrefersPlainText(string? accept) => accept?.Contains("text/plain") == true;

    private static IResult MapUploadError(string error)
    {
        if (error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return ApiProblem.NotFound(error);
        if (error.Contains("read-only", StringComparison.OrdinalIgnoreCase))
            return ApiProblem.BadRequest("write_denied", error);
        if (error.Contains("access denied", StringComparison.OrdinalIgnoreCase) || error.Contains("cloud identity", StringComparison.OrdinalIgnoreCase))
            return ApiProblem.Forbidden("cloud_access_denied", error);
        if (error.Contains("Unsupported file extension", StringComparison.OrdinalIgnoreCase))
            return ApiProblem.BadRequest("unsupported_file_type", error);
        if (error.Contains($"{ValidationConstants.MaxFileNameLength} characters", StringComparison.OrdinalIgnoreCase))
            return ApiProblem.BadRequest("filename_too_long", error);
        return ApiProblem.BadRequest(error);
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

public record FileContentResponse(
    string DocumentId,
    string FileName,
    string Path,
    string? ContentType,
    long SizeBytes,
    DateTime CreatedAt,
    string Content);

public record FileReindexCheckResponse(
    string DocumentId,
    bool NeedsReindex,
    string Reason,
    string? CurrentHash,
    string? StoredHash,
    string? CurrentChunkingStrategy,
    string? StoredChunkingStrategy,
    string? CurrentEmbeddingModel,
    string? StoredEmbeddingModel);

public record BrowseEntry(
    string Name,
    string Path,
    bool IsFolder,
    long? SizeBytes,
    DateTime? LastModified,
    string? Status,
    string? Id);
