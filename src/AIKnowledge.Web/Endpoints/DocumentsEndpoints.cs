using AIKnowledge.Core;
using AIKnowledge.Core.Interfaces;
using AIKnowledge.Core.Utilities;
using Microsoft.AspNetCore.Mvc;

namespace AIKnowledge.Web.Endpoints;

public static class DocumentsEndpoints
{
    public static IEndpointRouteBuilder MapDocumentsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/containers/{containerId:guid}/files").WithTags("Files");

        // POST /api/containers/{containerId}/files - Upload file(s) to container
        group.MapPost("/", async (
            Guid containerId,
            [FromForm] IFormFileCollection files,
            [FromForm] string? path,
            [FromForm] ChunkingStrategy? strategy,
            [FromServices] IContainerStore containerStore,
            [FromServices] IKnowledgeFileSystem fileSystem,
            [FromServices] IIngestionQueue queue,
            CancellationToken ct) =>
        {
            if (!await containerStore.ExistsAsync(containerId, ct))
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            if (files.Count == 0)
                return Results.BadRequest(new { error = "No files provided" });

            var destinationPath = PathUtilities.NormalizeFolderPath(path ?? "/");
            var uploadedDocs = new List<UploadedDocumentResponse>();
            string? batchId = files.Count > 1 ? Guid.NewGuid().ToString() : null;

            foreach (var file in files)
            {
                var documentId = Guid.NewGuid().ToString();
                var filePath = PathUtilities.NormalizePath($"{destinationPath}{file.FileName}");

                try
                {
                    // Stream file to storage
                    using var stream = file.OpenReadStream();
                    await fileSystem.SaveFileAsync(filePath, stream, ct);

                    // Enqueue ingestion job
                    var job = new IngestionJob(
                        JobId: Guid.NewGuid().ToString(),
                        DocumentId: documentId,
                        Path: filePath,
                        Options: new IngestionOptions(
                            DocumentId: documentId,
                            FileName: file.FileName,
                            ContentType: file.ContentType,
                            ContainerId: containerId.ToString(),
                            Path: filePath,
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
                        Path: filePath));
                }
                catch (Exception ex)
                {
                    uploadedDocs.Add(new UploadedDocumentResponse(
                        DocumentId: documentId,
                        JobId: null,
                        FileName: file.FileName,
                        SizeBytes: file.Length,
                        Path: filePath,
                        Error: ex.Message));
                }
            }

            return Results.Ok(new UploadResponse(
                BatchId: batchId,
                Documents: uploadedDocs,
                TotalCount: files.Count,
                SuccessCount: uploadedDocs.Count(d => d.Error == null)));
        })
        .DisableAntiforgery()
        .WithName("UploadFiles")
        .WithDescription("Upload one or more files to a container");

        // GET /api/containers/{containerId}/files - List files and folders at path
        group.MapGet("/", async (
            Guid containerId,
            [FromQuery] string? path,
            [FromServices] IContainerStore containerStore,
            [FromServices] IDocumentStore documentStore,
            [FromServices] IFolderStore folderStore,
            CancellationToken ct) =>
        {
            if (!await containerStore.ExistsAsync(containerId, ct))
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            var browsePath = PathUtilities.NormalizeFolderPath(path ?? "/");
            var entries = new List<BrowseEntry>();

            // Get explicit folders at this level
            var folders = await folderStore.ListAsync(containerId, parentPath: browsePath, ct);
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

            // Get documents at this path level
            var documents = await documentStore.ListAsync(containerId, pathPrefix: browsePath, ct);
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

            return Results.Ok(entries);
        })
        .WithName("ListFiles")
        .WithDescription("List files and folders at a path within a container");

        // GET /api/containers/{containerId}/files/{fileId} - Get file details
        group.MapGet("/{fileId}", async (
            Guid containerId,
            string fileId,
            [FromServices] IContainerStore containerStore,
            [FromServices] IDocumentStore documentStore,
            CancellationToken ct) =>
        {
            if (!await containerStore.ExistsAsync(containerId, ct))
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            var document = await documentStore.GetAsync(fileId, ct);
            if (document is null || document.ContainerId != containerId.ToString())
                return Results.NotFound(new { error = $"File {fileId} not found in container {containerId}" });

            return Results.Ok(document);
        })
        .WithName("GetFile")
        .WithDescription("Get file details including indexing status");

        // DELETE /api/containers/{containerId}/files/{fileId} - Delete file
        group.MapDelete("/{fileId}", async (
            Guid containerId,
            string fileId,
            [FromServices] IContainerStore containerStore,
            [FromServices] IDocumentStore documentStore,
            [FromServices] IKnowledgeFileSystem fileSystem,
            CancellationToken ct) =>
        {
            if (!await containerStore.ExistsAsync(containerId, ct))
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            var document = await documentStore.GetAsync(fileId, ct);
            if (document is null || document.ContainerId != containerId.ToString())
                return Results.NotFound(new { error = $"File {fileId} not found in container {containerId}" });

            // Delete from database (cascades to chunks and vectors)
            await documentStore.DeleteAsync(fileId, ct);

            // Delete file from storage (best effort)
            try
            {
                if (!string.IsNullOrEmpty(document.Path))
                    await fileSystem.DeleteAsync(document.Path, ct);
            }
            catch { /* File already deleted or not found */ }

            return Results.NoContent();
        })
        .WithName("DeleteFile")
        .WithDescription("Delete a file and all associated chunks and vectors");

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
        .WithDescription("Check if a specific file needs reindexing and why");

        return app;
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
