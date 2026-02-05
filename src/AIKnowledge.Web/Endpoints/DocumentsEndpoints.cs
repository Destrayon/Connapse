using AIKnowledge.Core;
using AIKnowledge.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AIKnowledge.Web.Endpoints;

public static class DocumentsEndpoints
{
    public static IEndpointRouteBuilder MapDocumentsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/documents").WithTags("Documents");

        // POST /api/documents - Upload document(s)
        group.MapPost("/", async (
            [FromForm] IFormFileCollection files,
            [FromForm] string? collectionId,
            [FromForm] string? destinationPath,
            [FromForm] ChunkingStrategy? strategy,
            [FromServices] IKnowledgeFileSystem fileSystem,
            [FromServices] IIngestionQueue queue,
            CancellationToken ct) =>
        {
            if (files.Count == 0)
                return Results.BadRequest(new { error = "No files provided" });

            var uploadedDocs = new List<UploadedDocumentResponse>();
            string? batchId = files.Count > 1 ? Guid.NewGuid().ToString() : null;

            foreach (var file in files)
            {
                // Generate document ID and virtual path
                var documentId = Guid.NewGuid().ToString();
                var virtualPath = $"{destinationPath?.TrimEnd('/') ?? "/uploads"}/{file.FileName}";

                try
                {
                    // Stream file to storage
                    using var stream = file.OpenReadStream();
                    await fileSystem.SaveFileAsync(virtualPath, stream, ct);

                    // Enqueue ingestion job
                    var job = new IngestionJob(
                        JobId: Guid.NewGuid().ToString(),
                        DocumentId: documentId,
                        VirtualPath: virtualPath,
                        Options: new IngestionOptions(
                            FileName: file.FileName,
                            ContentType: file.ContentType,
                            CollectionId: collectionId,
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
                        VirtualPath: virtualPath));
                }
                catch (Exception ex)
                {
                    uploadedDocs.Add(new UploadedDocumentResponse(
                        DocumentId: documentId,
                        JobId: null,
                        FileName: file.FileName,
                        SizeBytes: file.Length,
                        VirtualPath: virtualPath,
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
        .WithName("UploadDocuments")
        .WithDescription("Upload one or more documents for ingestion");

        // GET /api/documents - List all documents
        group.MapGet("/", async (
            [FromQuery] string? collectionId,
            [FromServices] IDocumentStore documentStore,
            CancellationToken ct) =>
        {
            var documents = await documentStore.ListAsync(collectionId, ct);
            return Results.Ok(documents);
        })
        .WithName("ListDocuments")
        .WithDescription("List all documents, optionally filtered by collection");

        // GET /api/documents/{id} - Get document by ID
        group.MapGet("/{id}", async (
            string id,
            [FromServices] IDocumentStore documentStore,
            CancellationToken ct) =>
        {
            var document = await documentStore.GetAsync(id, ct);
            return document is not null
                ? Results.Ok(document)
                : Results.NotFound(new { error = $"Document {id} not found" });
        })
        .WithName("GetDocument")
        .WithDescription("Get a specific document by ID");

        // DELETE /api/documents/{id} - Delete document
        group.MapDelete("/{id}", async (
            string id,
            [FromServices] IDocumentStore documentStore,
            [FromServices] IKnowledgeFileSystem fileSystem,
            CancellationToken ct) =>
        {
            var document = await documentStore.GetAsync(id, ct);
            if (document is null)
                return Results.NotFound(new { error = $"Document {id} not found" });

            // Delete from database (cascades to chunks and vectors)
            await documentStore.DeleteAsync(id, ct);

            // Delete file from storage (best effort - don't fail if file missing)
            try
            {
                var virtualPath = document.Metadata.GetValueOrDefault("VirtualPath");
                if (!string.IsNullOrEmpty(virtualPath))
                    await fileSystem.DeleteAsync(virtualPath, ct);
            }
            catch { /* File already deleted or not found - ignore */ }

            return Results.NoContent();
        })
        .WithName("DeleteDocument")
        .WithDescription("Delete a document and all associated chunks and vectors");

        // POST /api/documents/reindex - Trigger reindexing
        group.MapPost("/reindex", async (
            [FromBody] ReindexRequest? request,
            [FromServices] IReindexService reindexService,
            CancellationToken ct) =>
        {
            var options = new ReindexOptions
            {
                CollectionId = request?.CollectionId,
                DocumentIds = request?.DocumentIds,
                Force = request?.Force ?? false,
                DetectSettingsChanges = request?.DetectSettingsChanges ?? true,
                Strategy = request?.Strategy
            };

            var result = await reindexService.ReindexAsync(options, ct);

            return Results.Ok(new
            {
                batchId = result.BatchId,
                totalDocuments = result.TotalDocuments,
                enqueuedCount = result.EnqueuedCount,
                skippedCount = result.SkippedCount,
                failedCount = result.FailedCount,
                reasonCounts = result.ReasonCounts.ToDictionary(
                    kvp => kvp.Key.ToString(),
                    kvp => kvp.Value),
                message = $"Reindex complete: {result.EnqueuedCount} enqueued, {result.SkippedCount} skipped, {result.FailedCount} failed"
            });
        })
        .WithName("ReindexDocuments")
        .WithDescription("Trigger reindexing of documents with content-hash comparison and settings-change detection");

        // GET /api/documents/{id}/reindex-check - Check if document needs reindexing
        group.MapGet("/{id}/reindex-check", async (
            string id,
            [FromServices] IReindexService reindexService,
            CancellationToken ct) =>
        {
            var check = await reindexService.CheckDocumentAsync(id, ct);
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
        .WithName("CheckDocumentReindex")
        .WithDescription("Check if a specific document needs reindexing and why");

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
    string VirtualPath,
    string? Error = null);

public record ReindexRequest(
    string? CollectionId = null,
    IReadOnlyList<string>? DocumentIds = null,
    bool? Force = null,
    bool? DetectSettingsChanges = null,
    ChunkingStrategy? Strategy = null);
