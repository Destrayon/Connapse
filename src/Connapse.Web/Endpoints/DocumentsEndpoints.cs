using System.Security.Claims;
using System.Text;
using Connapse.Application;
using Connapse.Application.Files;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Web.Problems;
using Connapse.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
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
            HttpContext http,
            Guid containerId,
            [FromQuery] string? path,
            [FromQuery] int? skip,
            [FromQuery] int? take,
            [FromServices] ListFiles list,
            CancellationToken ct) =>
        {
            var listed = await list.RunAsync(Rest(http), containerId, new ListFilesQuery(path, skip ?? 0, take ?? 50), ct);
            if (!listed.Succeeded)
                return OperationProblems.Result(listed);

            var page = listed.Result!;
            return Results.Ok(new PagedResponse<BrowseEntry>(
                [.. page.Entries.Select(e => new BrowseEntry(e.Name, e.Path, e.IsFolder, e.SizeBytes, e.LastModified,
                    e.Document?.Metadata.GetValueOrDefault("Status"), e.Id))],
                page.TotalCount, page.HasMore));
        })
        .Produces<PagedResponse<BrowseEntry>>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("ListFiles")
        .WithDescription("List files and folders at a path within a container (?skip=0&take=50)")
        .RequireAuthorization("RequireViewer");

        // GET /api/containers/{containerId}/files/{fileId} - Get file details
        group.MapGet("/{fileId}", async (
            HttpContext http,
            Guid containerId,
            string fileId,
            [FromServices] GetFile get,
            CancellationToken ct) =>
        {
            var read = await get.RunAsync(Rest(http), containerId, fileId, ct);
            return read.Succeeded ? Results.Ok(read.Result) : OperationProblems.Result(read);
        })
        .Produces<Document>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("GetFile")
        .WithDescription("Get file details including indexing status")
        .RequireAuthorization("RequireViewer");

        // DELETE /api/containers/{containerId}/files/{fileId} - Delete file
        group.MapDelete("/{fileId}", async (
            HttpContext http,
            Guid containerId,
            string fileId,
            [FromServices] DeleteFile delete,
            CancellationToken ct) =>
        {
            var deleted = await delete.RunAsync(Rest(http), containerId, fileId, ct);
            return deleted.Succeeded ? Results.NoContent() : OperationProblems.Result(deleted);
        })
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("DeleteFile")
        .WithDescription("Delete a file and all associated chunks and vectors")
        .RequireAuthorization("RequireEditor");

        // POST /api/containers/{containerId}/files/bulk-delete - Delete up to 100 files, each reported on
        group.MapPost("/bulk-delete", async (
            HttpContext http,
            Guid containerId,
            [FromBody] BulkDeleteFilesRequest request,
            [FromServices] BulkDeleteFiles bulkDelete,
            CancellationToken ct) =>
        {
            var deleted = await bulkDelete.RunAsync(Rest(http), containerId, request.FileIds ?? [], ct);
            if (!deleted.Succeeded)
                return OperationProblems.Result(deleted);

            var items = deleted.Result!;
            return Results.Ok(new BulkDeleteFilesResponse([.. items], items.Count(i => i.Deleted), items.Count(i => !i.Deleted)));
        })
        .Produces<BulkDeleteFilesResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("files.bulkDelete")
        .WithDescription("Delete up to 100 files in one call. Answers with each file's result; one file failing doesn't fail the others.")
        // The scope too, so an Editor's knowledge:read token can't delete.
        .RequireAuthorization("RequireEditor", "Scope:KnowledgeWrite");

        // GET /api/containers/{containerId}/files/{fileId}/content - Get file text content
        group.MapGet("/{fileId}/content", async (
            HttpContext http,
            Guid containerId,
            string fileId,
            [FromServices] GetFileContent content,
            CancellationToken ct) =>
        {
            var read = await content.RunAsync(Rest(http), containerId, fileId, ct);
            if (!read.Succeeded)
                return OperationProblems.Result(read);

            var (document, text) = read.Result!;
            // Content negotiation
            if (PrefersPlainText(http.Request.Headers.Accept.ToString()))
                return Results.Text(text, "text/plain");

            return Results.Ok(new FileContentResponse(
                document.Id, document.FileName, document.Path, document.ContentType, document.SizeBytes, document.CreatedAt, text));
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
            HttpContext http,
            Guid containerId,
            string fileId,
            [FromServices] CheckFileReindex check,
            CancellationToken ct) =>
        {
            var checkedFile = await check.RunAsync(Rest(http), containerId, fileId, ct);
            if (!checkedFile.Succeeded)
                return OperationProblems.Result(checkedFile);

            var result = checkedFile.Result!;
            return Results.Ok(new FileReindexCheckResponse(
                result.DocumentId,
                result.NeedsReindex,
                result.Reason.ToString(),
                result.CurrentHash,
                result.StoredHash,
                result.CurrentChunkingStrategy,
                result.StoredChunkingStrategy,
                result.CurrentEmbeddingModel,
                result.StoredEmbeddingModel));
        })
        .Produces<FileReindexCheckResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("CheckFileReindex")
        .WithDescription("Check if a specific file needs reindexing and why")
        .RequireAuthorization("RequireViewer");

        // POST /api/containers/{containerId}/files/{fileId}/retry - Queue one file's ingestion again
        group.MapPost("/{fileId}/retry", async (
            HttpContext http,
            Guid containerId,
            string fileId,
            [FromServices] RetryFile retry,
            CancellationToken ct) =>
        {
            var queued = await retry.RunAsync(Rest(http), containerId, fileId, ct);
            return queued.Succeeded ? Results.Accepted(value: queued.Result) : OperationProblems.Result(queued);
        })
        .Produces<FileRetry>(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("files.retry")
        .WithDescription("Queue one file's ingestion again, with a fresh attempt budget. Answers with the ingestion job's id.")
        .RequireAuthorization("RequireEditor", "Scope:KnowledgeWrite");

        return app;
    }

    /// <summary>
    /// Whether a file's content goes back as plain text rather than JSON, for this Accept header: when the
    /// client rates text/plain above application/json, or as high but by a more specific range, such as
    /// <c>text/plain, */*</c>. A tie, or no header, is JSON.
    /// </summary>
    public static bool PrefersPlainText(string? accept)
    {
        if (string.IsNullOrWhiteSpace(accept)
            || !MediaTypeHeaderValue.TryParseList([accept], out var ranges))
            return false;

        var text = Preference(ranges, "text", "plain");
        var json = Preference(ranges, "application", "json");
        return text.Quality > 0
            && (text.Quality > json.Quality || (text.Quality == json.Quality && text.Specificity > json.Specificity));
    }

    /// <summary>The quality the most specific matching range gives a media type, and how specific that range is.</summary>
    private static (double Quality, int Specificity) Preference(
        IList<MediaTypeHeaderValue> ranges, string type, string subType)
    {
        (double Quality, int Specificity) best = (0, -1);
        foreach (var range in ranges)
        {
            int specificity = range.MatchesAllTypes ? 0
                : !range.Type.Equals(type, StringComparison.OrdinalIgnoreCase) ? -1
                : range.MatchesAllSubTypes ? 1
                : range.SubType.Equals(subType, StringComparison.OrdinalIgnoreCase) ? 2
                : -1;
            if (specificity > best.Specificity)
                best = (range.Quality ?? 1, specificity);
        }
        return best;
    }

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

    private static OperationContext Rest(HttpContext http) => new(http.User, OperationSurface.Rest);

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

public record BulkDeleteFilesRequest(List<string>? FileIds);

/// <summary>What a bulk delete did with each file, in the order asked, and how many were deleted.</summary>
public record BulkDeleteFilesResponse(List<BulkDeleteItem> Results, int DeletedCount, int FailedCount);

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
