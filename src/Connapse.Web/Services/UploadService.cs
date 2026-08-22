using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;

namespace Connapse.Web.Services;

public class UploadService(
    IContainerStore containerStore,
    IManagedStorageProvider managedStorage,
    IFolderStore folderStore,
    IIngestionQueue ingestionQueue,
    IDocumentStore documentStore,
    IFileTypeValidator fileTypeValidator,
    IAuditLogger auditLogger) : IUploadService
{
    private static readonly Dictionary<string, string> ContentTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".pdf"] = "application/pdf",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".doc"] = "application/msword",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".csv"] = "text/csv",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".html"] = "text/html",
        [".htm"] = "text/html",
    };

    public async Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken ct = default)
    {
        // 1-4: Input validation (cheap, no I/O)
        var validationError = ValidateInput(request);
        if (validationError is not null)
            return new UploadResult(false, Error: validationError);

        // 5: Container existence
        var container = await containerStore.GetAsync(request.ContainerId, ct);
        if (container is null)
            return new UploadResult(false, Error: "Container not found.");

        // 6: Write access. Managed storage is the only writable backend, so resolving an
        // IWritableConnector *is* the permission check, so no runtime guard is needed.
        if (managedStorage.CreateConnector(container.Id) is not IWritableConnector connector)
            return new UploadResult(false, Error: "This container is not writable.");

        return await ExecuteUploadAsync(request, container, connector, null, ct);
    }

    public async Task<BulkUploadResult> BulkUploadAsync(BulkUploadRequest request, CancellationToken ct = default)
    {
        // Container-level validation (once)
        var container = await containerStore.GetAsync(request.ContainerId, ct);
        if (container is null)
            return new BulkUploadResult(0, request.Files.Count, Results: request.Files
                .Select(_ => new UploadResult(false, Error: "Container not found.")).ToList());

        if (managedStorage.CreateConnector(container.Id) is not IWritableConnector connector)
            return new BulkUploadResult(0, request.Files.Count, Results: request.Files
                .Select(_ => new UploadResult(false, Error: "This container is not writable.")).ToList());

        var batchId = Guid.NewGuid().ToString();
        var results = new List<UploadResult>();

        foreach (var file in request.Files)
        {
            var validationError = ValidateInput(file);
            if (validationError is not null)
            {
                results.Add(new UploadResult(false, Error: validationError));
                continue;
            }

            try
            {
                var result = await ExecuteUploadAsync(file, container, connector, batchId, ct);
                results.Add(result);
            }
            catch (Exception ex)
            {
                results.Add(new UploadResult(false, Error: $"Upload failed: {ex.Message}"));
            }
        }

        return new BulkUploadResult(
            results.Count(r => r.Success),
            results.Count(r => !r.Success),
            batchId,
            results);
    }

    private string? ValidateInput(UploadRequest request)
    {
        if (request.FileName.Length > ValidationConstants.MaxFileNameLength)
            return $"Filename exceeds {ValidationConstants.MaxFileNameLength} characters.";

        if (!PathUtilities.IsValidFileName(request.FileName))
            return $"Invalid filename: '{request.FileName}'.";

        if (request.Path is not null && PathUtilities.ContainsPathTraversal(request.Path))
            return "Path traversal is not allowed.";

        if (request.Path is not null)
        {
            var segments = request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > ValidationConstants.MaxPathDepth)
                return $"Path exceeds maximum depth of {ValidationConstants.MaxPathDepth} levels.";
        }

        if (!fileTypeValidator.IsSupported(request.FileName))
        {
            var supported = string.Join(", ", fileTypeValidator.SupportedExtensions.OrderBy(e => e));
            return $"Unsupported file extension. Supported types: {supported}";
        }

        if (request.Content.CanSeek && request.Content.Length == 0)
            return "File is empty. Zero-byte uploads are not allowed.";

        return null;
    }

    private async Task<UploadResult> ExecuteUploadAsync(
        UploadRequest request,
        Container container,
        IWritableConnector connector,
        string? batchId,
        CancellationToken ct)
    {
        var documentId = Guid.NewGuid().ToString();
        var jobId = Guid.NewGuid().ToString();

        var normalizedPath = PathUtilities.NormalizePath(request.Path ?? "/");
        var virtualFilePath = normalizedPath == "/"
            ? $"/{request.FileName}"
            : $"{normalizedPath}/{request.FileName}";
        var relativePath = virtualFilePath.TrimStart('/');

        var contentType = request.ContentType ?? InferContentType(request.FileName);

        // Write file
        await connector.WriteFileAsync(relativePath, request.Content, contentType, ct);

        // Ensure intermediate folders
        var folderPath = PathUtilities.GetParentPath(virtualFilePath);
        if (folderPath != "/")
            await EnsureIntermediateFoldersAsync(folderStore, request.ContainerId, folderPath, ct);

        // Resolve job path
        var jobPath = connector.ResolveJobPath(relativePath);

        // Cancel any in-flight ingestion for an existing document at the same path.
        var existingDoc = await documentStore.GetByPathAsync(request.ContainerId, virtualFilePath, ct);
        if (existingDoc is not null)
            await ingestionQueue.CancelJobForDocumentAsync(existingDoc.Id);

        // Eagerly create/update the document row. The upsert atomically increments
        // the generation counter, so any in-flight job for a prior generation will
        // detect it's stale and skip chunk insertion.
        var storeResult = await documentStore.StoreAsync(new Document(
            documentId,
            request.ContainerId.ToString(),
            request.FileName,
            contentType,
            virtualFilePath,
            request.Content.CanSeek ? request.Content.Length : 0,
            DateTime.UtcNow,
            new Dictionary<string, string>
            {
                ["OriginalFileName"] = request.FileName,
                ["UploadedAt"] = DateTime.UtcNow.ToString("O"),
                ["IngestedVia"] = request.IngestedVia
            }), ct);

        // Use the winning document ID (may differ from our generated one if upsert hit an existing row)
        var winnerDocId = storeResult.DocumentId;

        // Parse strategy
        var strategy = ChunkingStrategy.Semantic;
        if (request.Strategy is not null &&
            Enum.TryParse<ChunkingStrategy>(request.Strategy, true, out var parsed))
            strategy = parsed;

        // Build and enqueue ingestion job with the current generation
        var job = new IngestionJob(
            JobId: jobId,
            DocumentId: winnerDocId,
            Path: jobPath,
            Options: new IngestionOptions(
                DocumentId: winnerDocId,
                FileName: request.FileName,
                ContentType: contentType,
                ContainerId: request.ContainerId.ToString(),
                Path: virtualFilePath,
                Strategy: strategy,
                Metadata: new Dictionary<string, string>
                {
                    ["OriginalFileName"] = request.FileName,
                    ["UploadedAt"] = DateTime.UtcNow.ToString("O"),
                    ["IngestedVia"] = request.IngestedVia
                },
                Generation: storeResult.Generation),
            Generation: storeResult.Generation,
            BatchId: batchId);

        await ingestionQueue.EnqueueAsync(job, ct);

        // Audit log
        await auditLogger.LogAsync("doc.uploaded", "document", winnerDocId,
            new { FileName = request.FileName, ContainerId = request.ContainerId, Via = request.IngestedVia }, ct);

        return new UploadResult(true, winnerDocId, jobId);
    }

    private static string InferContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext is not null && ContentTypeMap.TryGetValue(ext, out var ct) ? ct : "application/octet-stream";
    }

    private static async Task EnsureIntermediateFoldersAsync(
        IFolderStore folderStore, Guid containerId, string normalizedFolderPath, CancellationToken ct)
    {
        if (normalizedFolderPath == "/")
            return;

        var segments = normalizedFolderPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentPath = "/";

        foreach (var segment in segments)
        {
            currentPath += segment + "/";
            if (!await folderStore.ExistsAsync(containerId, currentPath, ct))
                await folderStore.CreateAsync(containerId, currentPath, ct);
        }
    }
}
