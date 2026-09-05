using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Vectors;
using Connapse.Web.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;

namespace Connapse.Web.Mcp;

[McpServerToolType]
public class McpTools
{
    // Threshold above which `list_files` returns a soft error steering the agent to
    // `search_knowledge`. The agent can override with `confirmLarge: true` or paginate with `limit`.
    internal const int ListFilesSoftLimit = 50;


    [McpServerTool(Name = "container_create"),
     Description("Create a new container for organizing documents. Use when setting up a new knowledge domain or project.")]
    public static async Task<string> ContainerCreate(
        IServiceProvider services,
        [Description("Container name (lowercase alphanumeric and hyphens, 2-128 chars)")] string name,
        [Description("Optional description for the container")] string? description = null,
        CancellationToken ct = default)
    {
        name = name.Trim();

        if (!PathUtilities.IsValidContainerName(name))
            return "Error: Container name must be 2-128 chars, lowercase alphanumeric and hyphens.";

        var containerStore = services.GetRequiredService<IContainerStore>();

        var existing = await containerStore.GetByNameAsync(name, ct);
        if (existing is not null)
            return $"Error: Container '{name}' already exists.";

        var container = await containerStore.CreateAsync(new CreateContainerRequest(name, description), ct);
        return $"Container '{container.Name}' created.\n\nID: {container.Id}";
    }

    [McpServerTool(Name = "container_list", ReadOnly = true, Idempotent = true),
     Description("Lists every searchable knowledge scope with its description and document count. Each entry is either kind=managed (storage Connapse owns, browsable with `list_files`) or kind=source (an external system Connapse mirrors read-only — searchable, but it has no file listing). Use to discover what exists when the target is unknown; if the user already named one, call `search_knowledge` on it directly instead.")]
    public static async Task<string> ContainerList(
        IServiceProvider services,
        CancellationToken ct = default)
    {
        var containerStore = services.GetRequiredService<IContainerStore>();
        var sourceStore = services.GetRequiredService<ISourceStore>();

        var containers = await containerStore.ListAsync(take: int.MaxValue, ct: ct);
        var sources = await sourceStore.ListAsync(take: int.MaxValue, ct: ct);

        // Sources are listed alongside containers so existing agent prompts and the CLI keep
        // working — a source is simply a searchable scope that happens to be read-only. The
        // kind field is what lets an agent avoid calling list_files on one.
        var owners = containers.Select(ToOwner).OfType<SearchableOwner>()
            .Concat(sources.Select(ToOwner))
            .ToList();

        if (owners.Count == 0)
            return "No containers found.";

        // Conditional TIP: only emit when there's an actual routing decision to make
        // (i.e., more than one container). For a single container the agent has no
        // choice; the TIP would be wasted output tokens.
        var text = "";
        if (owners.Count > 1)
        {
            text = "TIP: Pick the entry whose description best matches the topic, then call `search_knowledge(query=\"...\", containerId=\"<name>\")`.\n\n";
        }

        text += $"Found {owners.Count} knowledge scope(s):\n\n";
        foreach (var owner in owners)
        {
            text += $"- {owner.Name} [{owner.Kind}] ({owner.DocumentCount} files)";
            if (!string.IsNullOrEmpty(owner.Description))
                text += $" — {owner.Description}";
            text += "\n";

            // Append summary first sentence if available
            string? firstSentence = TruncateToFirstSentence(owner.Summary, maxChars: 120);
            if (!string.IsNullOrEmpty(firstSentence))
            {
                text += $"  Summary: {firstSentence}\n";
            }

            text += $"  ID: {owner.Id}\n";
        }

        return text.TrimEnd();
    }

    [McpServerTool(Name = "container_delete", Destructive = true),
     Description("Delete a container. It must be emptied first, because deleting one deletes its stored files. External sources are not containers and cannot be deleted here.")]
    public static async Task<string> ContainerDelete(
        IServiceProvider services,
        [Description("Container ID or name")] string containerId,
        CancellationToken ct = default)
    {
        var containerStore = services.GetRequiredService<IContainerStore>();

        var resolvedId = await ResolveContainerIdAsync(containerId, containerStore, ct);
        if (resolvedId is null)
            return $"Error: Container '{containerId}' not found.";

        var container = await containerStore.GetAsync(resolvedId.Value, ct);

        var deleted = await containerStore.DeleteAsync(resolvedId.Value, ct);
        if (!deleted)
            return $"Error: Container '{containerId}' is not empty. Delete all files first.";

        var auditLogger = services.GetRequiredService<IAuditLogger>();
        await auditLogger.LogAsync("container.deleted", "container", resolvedId.Value.ToString(),
            new { Name = container?.Name ?? containerId }, ct);

        return $"Container '{containerId}' deleted.";
    }

    [McpServerTool(Name = "search_knowledge", ReadOnly = true, Idempotent = true),
     Description("Search a container using semantic, keyword, or hybrid mode (Hybrid is the default and works well without tuning). Returns ranked passages with citations, scores, and document IDs. If the first query returns thin results, refine the query and call again.")]
    public static async Task<string> SearchKnowledge(
        IServiceProvider services,
        [Description("The search query text")] string query,
        [Description("Container or source ID or name to search within")] string containerId,
        [Description("Search mode: Semantic (vector), Keyword (full-text), or Hybrid (both). Default: Hybrid")] string? mode = null,
        [Description("Number of results to return. Default: 10")] int? topK = null,
        [Description("Optional: Filter results to a folder subtree (e.g., '/docs/')")] string? path = null,
        [Description("Minimum similarity score floor (0.0-1.0). Defaults to 0.05.")] float? minScore = null,
        CancellationToken ct = default)
    {
        var containerStore = services.GetRequiredService<IContainerStore>();
        var sourceStore = services.GetRequiredService<ISourceStore>();

        // Accepts either kind: search is scoped by owner_id, which is the same column
        // whichever kind owns the document, so a source needs no separate search path.
        var owner = await ResolveSearchableOwnerAsync(containerId, containerStore, sourceStore, ct);
        if (owner is null)
            return $"Error: Container '{containerId}' not found.";

        Guid? resolvedId = owner.Id;

        if (query.Length > ValidationConstants.MaxQueryLength)
            throw new ArgumentException($"Query must not exceed {ValidationConstants.MaxQueryLength} characters.");

        if (topK.HasValue && (topK.Value < ValidationConstants.MinTopK || topK.Value > ValidationConstants.MaxTopK))
            throw new ArgumentException($"topK must be between {ValidationConstants.MinTopK} and {ValidationConstants.MaxTopK}.");

        if (minScore.HasValue && (minScore.Value < ValidationConstants.MinScore || minScore.Value > ValidationConstants.MaxScore))
            throw new ArgumentException($"minScore must be between {ValidationConstants.MinScore:F1} and {ValidationConstants.MaxScore:F1}.");

        var parsedMode = Enum.TryParse<SearchMode>(mode, ignoreCase: true, out var m) ? m : SearchMode.Hybrid;
        var effectiveTopK = topK ?? 10;

        float effectiveMinScore;
        if (minScore.HasValue)
        {
            effectiveMinScore = minScore.Value;
        }
        else
        {
            var searchSettings = services.GetRequiredService<IOptionsMonitor<SearchSettings>>();
            effectiveMinScore = (float)searchSettings.CurrentValue.MinimumScore;
        }

        Dictionary<string, string>? filters = null;
        if (!string.IsNullOrWhiteSpace(path))
            filters = new Dictionary<string, string> { ["pathPrefix"] = path };

        // Through IHttpContextAccessor because an MCP tool is handed only an IServiceProvider.
        // The accessor is registered for this: without it these tools are the one surface that
        // cannot name its caller, and a permission filter is only as good as its weakest entry
        // point.
        var caller = services.GetService<IHttpContextAccessor>()?.HttpContext?.User;

        var options = new SearchOptions(
            Mode: parsedMode,
            TopK: effectiveTopK,
            MinScore: effectiveMinScore,
            ContainerId: resolvedId.Value.ToString(),
            Filters: filters,
            UserId: SearchPrincipal.Resolve(caller));

        var searchService = services.GetRequiredService<IKnowledgeSearch>();
        var result = await searchService.SearchAsync(query, options, ct);

        if (result.Hits.Count == 0)
            return "No results found.";

        var countSummary = result.Hits.Count < result.TotalMatches
            ? $"Showing {result.Hits.Count} of {result.TotalMatches} matching chunk(s)"
            : $"Found {result.TotalMatches} result(s)";
        var resultText = $"{countSummary} in {result.Duration.TotalMilliseconds:F0}ms (mode: {parsedMode}):\n\n";
        for (var i = 0; i < result.Hits.Count; i++)
        {
            var hit = result.Hits[i];
            var meta = hit.Metadata;
            meta.TryGetValue("fileName", out var fileName);
            meta.TryGetValue("path", out var docPath);
            meta.TryGetValue("chunkIndex", out var chunkIndex);

            resultText += $"--- Result {i + 1} ---\n";
            resultText += $"Score: {hit.Score:F3}\n";
            resultText += $"File: {fileName ?? "unknown"}\n";
            resultText += $"Path: {docPath ?? "/"}\n";
            resultText += $"Chunk: {chunkIndex ?? "0"}\n";
            resultText += $"DocumentId: {hit.DocumentId}\n";
            resultText += $"Content:\n{hit.Content}\n\n";
        }

        return resultText.TrimEnd();
    }

    [McpServerTool(Name = "list_files", ReadOnly = true, Idempotent = true),
     Description("Lists folder entries and document IDs at a path within a container (does NOT return file contents — for content questions, use `search_knowledge`). Intended for inventory requests such as 'what files exist in X' or when the user named a specific filename. Large listings return a soft error directing you to `search_knowledge`; override with `confirmLarge: true` or paginate with `limit`.")]
    public static async Task<string> ListFiles(
        IServiceProvider services,
        [Description("Container ID or name")] string containerId,
        [Description("Folder path to list (default: root '/')")] string? path = null,
        [Description($"Maximum number of entries to return. When set, large listings are truncated instead of returning a soft error. Recommended: 25.")] int? limit = null,
        [Description("Set to true to confirm you intentionally want the full listing of a large folder. Without this, listings exceeding the soft limit return an error directing you to `search_knowledge`.")] bool? confirmLarge = null,
        CancellationToken ct = default)
    {
        // Validate `limit` upfront. A non-positive value would otherwise bypass the
        // soft-error guard (`!limit.HasValue` is false when limit is set) and the
        // rendering loop would short-circuit at `rendered >= effectiveLimit`,
        // producing misleading "(empty)" output for non-empty folders.
        if (limit.HasValue && limit.Value <= 0)
            return "Error: 'limit' must be greater than 0.";

        var folderPath = path ?? "/";

        var containerStore = services.GetRequiredService<IContainerStore>();
        var resolvedId = await ResolveContainerIdAsync(containerId, containerStore, ct);
        if (resolvedId is null)
            return $"Error: Container '{containerId}' not found.";

        var documentStore = services.GetRequiredService<IDocumentStore>();
        var folderStore = services.GetRequiredService<IFolderStore>();

        var normalizedPath = PathUtilities.NormalizeFolderPath(folderPath);

        var folders = await folderStore.ListAsync(resolvedId.Value, parentPath: normalizedPath, take: int.MaxValue, ct: ct);
        var documents = await documentStore.ListAsync(resolvedId.Value, pathPrefix: normalizedPath, take: int.MaxValue, ct: ct);

        // If non-root path has no folder record and no documents, it doesn't exist
        if (normalizedPath != "/" && folders.Count == 0 && documents.Count == 0)
            return $"Error: Folder '{normalizedPath}' not found in this container.";

        // Collect explicit folder names
        var folderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            folderNames.Add(PathUtilities.GetFileName(folder.Path.TrimEnd('/')));
        }

        // Derive implicit folder names from document paths (for existing uploads
        // that were created before folder entries were tracked)
        foreach (var doc in documents)
        {
            var docParent = PathUtilities.GetParentPath(doc.Path);
            if (string.Equals(docParent, normalizedPath, StringComparison.OrdinalIgnoreCase))
                continue; // Direct child file, not a subfolder indicator

            // Extract the immediate child directory name relative to normalizedPath
            var relative = doc.Path[normalizedPath.Length..];
            var slashIndex = relative.IndexOf('/');
            if (slashIndex > 0)
            {
                folderNames.Add(relative[..slashIndex]);
            }
        }

        // Direct-child files (the only docs that actually render at this level)
        var directChildDocs = documents
            .Where(d => string.Equals(PathUtilities.GetParentPath(d.Path), normalizedPath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        int visibleEntries = folderNames.Count + directChildDocs.Count;

        // Soft-error path: refuse to dump large listings unless agent explicitly opts in.
        // Teaches the agent to reach for `search_knowledge` instead of enumeration.
        if (visibleEntries > ListFilesSoftLimit && confirmLarge != true && !limit.HasValue)
        {
            return $"Error: '{normalizedPath}' contains {visibleEntries} entries, which exceeds the soft listing limit of {ListFilesSoftLimit}. " +
                   $"For question-answering, call `search_knowledge(query=\"...\", containerId=\"{containerId}\")` instead — it returns the relevant passages directly. " +
                   $"If you genuinely need the full inventory, retry with `confirmLarge: true`, or paginate with `limit: 25`.";
        }

        // Conditional TIP: only emit when at the container root with multiple entries —
        // the case where agent-wandering is the failure mode. Sub-folder listings and
        // single-entry listings are usually intentional targeting; TIP would be wasted.
        var text = "";
        if (normalizedPath == "/" && visibleEntries > 1)
        {
            text = $"TIP: This lists folder/file names only — for file contents, call `search_knowledge(query=\"...\", containerId=\"{containerId}\")`.\n\n";
        }
        text += $"Contents of {normalizedPath}:\n\n";
        bool hasEntries = false;
        int rendered = 0;
        int effectiveLimit = limit ?? int.MaxValue;

        foreach (var folderName in folderNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            if (rendered >= effectiveLimit) break;
            text += $"[DIR]  {folderName}/\n";
            hasEntries = true;
            rendered++;
        }

        foreach (var doc in directChildDocs)
        {
            if (rendered >= effectiveLimit) break;
            text += $"[FILE] {doc.FileName} ({doc.SizeBytes:N0} bytes) ID: {doc.Id}\n";
            hasEntries = true;
            rendered++;
        }

        if (!hasEntries)
            text += "(empty)\n";
        else if (rendered < visibleEntries)
            text += $"\n... {visibleEntries - rendered} more entries truncated (limit={effectiveLimit}). Call `search_knowledge` for content questions, or re-run with a higher `limit`.";

        return text.TrimEnd();
    }

    [McpServerTool(Name = "upload_file"),
     Description("Upload a file to be parsed, chunked, embedded, and made searchable. Provide either 'content' (base64) or 'textContent' (raw text), not both.")]
    public static async Task<string> UploadFile(
        IServiceProvider services,
        [Description("Container ID or name")] string containerId,
        [Description("Base64-encoded file content. For binary files (PDF, DOCX, images). Mutually exclusive with textContent.")] string? content = null,
        [Description("Raw text content for text-based files (Markdown, TXT, CSV, JSON, etc.). Mutually exclusive with content.")] string? textContent = null,
        [Description("Original file name with extension")] string fileName = "",
        [Description("Destination folder path (e.g., '/docs/2026/')")] string? path = null,
        [Description("Chunking strategy: Semantic, FixedSize, or Recursive. Default: Semantic")] string? strategy = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(fileName))
            return "Error: 'fileName' is required.";

        if (!PathUtilities.IsValidFileName(fileName))
            return $"Error: invalid filename '{fileName}' — must not contain path separators or '..' segments.";

        var fileTypeValidator = services.GetRequiredService<IFileTypeValidator>();
        if (!fileTypeValidator.IsSupported(fileName))
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            var supported = string.Join(", ", fileTypeValidator.SupportedExtensions.OrderBy(e => e));
            return $"Error: file type '{ext}' is not supported. Supported types: {supported}";
        }

        if (content is not null && textContent is not null)
            return "Error: Provide either 'content' or 'textContent', not both.";

        if (content is null && textContent is null)
            return "Error: Provide either 'content' (base64) or 'textContent' (raw text).";

        var containerStore = services.GetRequiredService<IContainerStore>();
        var resolvedId = await ResolveContainerIdAsync(containerId, containerStore, ct);
        if (resolvedId is null)
            return $"Error: Container '{containerId}' not found.";

        byte[] fileBytes;
        if (textContent is not null)
        {
            fileBytes = System.Text.Encoding.UTF8.GetBytes(textContent);
        }
        else
        {
            try
            {
                fileBytes = Convert.FromBase64String(content!);
            }
            catch
            {
                return "Error: 'content' must be valid base64-encoded data.";
            }
        }

        var uploadService = services.GetRequiredService<IUploadService>();
        var stream = new MemoryStream(fileBytes);
        var request = new UploadRequest(
            resolvedId.Value, fileName, stream, null, path, null, strategy, "MCP");

        var result = await uploadService.UploadAsync(request, ct);
        if (!result.Success)
            return $"Error: {result.Error}";

        var filePath = PathUtilities.NormalizePath(
            PathUtilities.NormalizeFolderPath(path ?? "/") + fileName);

        return $"File '{fileName}' uploaded to {filePath} and queued for ingestion.\n\n" +
               $"Document ID: {result.DocumentId}\nJob ID: {result.JobId}\n\n" +
               "The file will be parsed, chunked, and embedded in the background.";
    }

    [McpServerTool(Name = "delete_file", Destructive = true),
     Description("Delete a file and all its chunks and vectors. To update a file, delete it first then re-upload with upload_file.")]
    public static async Task<string> DeleteFile(
        IServiceProvider services,
        [Description("Container ID or name")] string containerId,
        [Description("File (document) ID to delete")] string fileId,
        CancellationToken ct = default)
    {
        var containerStore = services.GetRequiredService<IContainerStore>();
        var resolvedId = await ResolveContainerIdAsync(containerId, containerStore, ct);
        if (resolvedId is null)
            return $"Error: Container '{containerId}' not found.";

        var documentStore = services.GetRequiredService<IDocumentStore>();
        var document = await documentStore.GetAsync(fileId, ct);

        if (document is null || document.ContainerId != resolvedId.Value.ToString())
            return $"Error: File '{fileId}' not found in this container.";

        var ingestionQueue = services.GetRequiredService<IIngestionQueue>();
        await ingestionQueue.CancelJobForDocumentAsync(fileId);

        await documentStore.DeleteAsync(fileId, ct);

        // Clean up empty parent folders
        var folderStore = services.GetRequiredService<IFolderStore>();
        if (!string.IsNullOrEmpty(document.Path))
            await folderStore.DeleteEmptyAncestorsAsync(resolvedId.Value, document.Path, ct);

        var storageDeleteFailed = false;
        try
        {
            var fileSystem = services.GetRequiredService<IKnowledgeFileSystem>();
            if (!string.IsNullOrEmpty(document.Path))
                await fileSystem.DeleteAsync(document.Path, ct);
        }
        catch (Exception ex)
        {
            var logger = services.GetRequiredService<ILogger<McpTools>>();
            logger.LogWarning(ex, "Failed to delete backing file {Path}", document.Path);
            storageDeleteFailed = true;
        }

        return storageDeleteFailed
            ? $"File '{document.FileName}' (ID: {fileId}) deleted from database, but the backing storage file could not be removed and may need manual cleanup."
            : $"File '{document.FileName}' (ID: {fileId}) deleted.";
    }

    [McpServerTool(Name = "bulk_delete", Destructive = true),
     Description("Delete up to 100 files in one call. Returns per-file success/failure results.")]
    public static async Task<string> BulkDelete(
        IServiceProvider services,
        [Description("Container ID or name")] string containerId,
        [Description("JSON array of file (document) IDs to delete, e.g. [\"id1\",\"id2\"]. Max 100.")] string fileIds,
        CancellationToken ct = default)
    {
        List<string> ids;
        try
        {
            ids = System.Text.Json.JsonSerializer.Deserialize<List<string>>(fileIds) ?? [];
        }
        catch
        {
            return "Error: 'fileIds' must be a valid JSON array of strings.";
        }

        if (ids.Count == 0)
            return "Error: 'fileIds' array must not be empty.";

        if (ids.Count > 100)
            return "Error: Maximum 100 files per bulk_delete call.";

        // Early-exit if container doesn't exist (avoids N per-file "not found" errors).
        // Note: DeleteFile re-resolves the container internally — this is redundant but
        // keeps the top-level error message clean for missing containers.
        var containerStore = services.GetRequiredService<IContainerStore>();
        var resolvedId = await ResolveContainerIdAsync(containerId, containerStore, ct);
        if (resolvedId is null)
            return $"Error: Container '{containerId}' not found.";

        var succeeded = 0;
        var failures = new List<string>();
        var warnings = new List<string>();

        foreach (var fileId in ids)
        {
            var result = await DeleteFile(services, containerId, fileId, ct);

            if (result.StartsWith("Error:"))
            {
                failures.Add($"{fileId}: {result["Error: ".Length..]}");
            }
            else if (result.Contains("backing storage file"))
            {
                succeeded++;
                warnings.Add($"{fileId}: storage cleanup failed");
            }
            else
            {
                succeeded++;
            }
        }

        var summary = $"Deleted {succeeded} of {ids.Count} file(s).";
        if (warnings.Count > 0)
            summary += $"\n\nWarnings ({warnings.Count}):\n{string.Join("\n", warnings.Select(w => $"- {w}"))}";
        if (failures.Count > 0)
            summary += $"\n\nFailures:\n{string.Join("\n", failures.Select(f => $"- {f}"))}";

        return summary;
    }

    [McpServerTool(Name = "bulk_upload"),
     Description("Upload up to 100 files in one call. Each file is parsed, chunked, and embedded. Returns per-file results.")]
    public static async Task<string> BulkUpload(
        IServiceProvider services,
        [Description("Container ID or name")] string containerId,
        [Description("JSON array of file objects. Each object: {\"filename\":\"name.txt\", \"content\":\"...\", \"encoding\":\"text|base64\", \"folderPath\":\"/optional/\"}. Max 100.")] string files,
        CancellationToken ct = default)
    {
        List<BulkUploadFileItem> items;
        try
        {
            var jsonOptions = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            items = System.Text.Json.JsonSerializer.Deserialize<List<BulkUploadFileItem>>(files, jsonOptions) ?? [];
        }
        catch
        {
            return "Error: 'files' must be a valid JSON array of file objects.";
        }

        if (items.Count == 0)
            return "Error: 'files' array must not be empty.";

        if (items.Count > 100)
            return "Error: Maximum 100 files per bulk_upload call.";

        var containerStore = services.GetRequiredService<IContainerStore>();
        var resolvedId = await ResolveContainerIdAsync(containerId, containerStore, ct);
        if (resolvedId is null)
            return $"Error: Container '{containerId}' not found.";

        var container = await containerStore.GetAsync(resolvedId.Value, ct);

        // Pre-validate and decode all items (transport-level concerns)
        var uploadRequests = new List<UploadRequest>();
        var transportFailures = new List<string>();

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var itemLabel = item.Filename ?? $"item[{i}]";

            if (string.IsNullOrWhiteSpace(item.Filename))
            {
                transportFailures.Add($"{itemLabel}: missing 'filename'");
                continue;
            }

            if (string.IsNullOrEmpty(item.Content))
            {
                transportFailures.Add($"{itemLabel}: missing 'content'");
                continue;
            }

            byte[] fileBytes;
            var isBase64 = string.Equals(item.Encoding, "base64", StringComparison.OrdinalIgnoreCase);
            if (isBase64)
            {
                try
                {
                    fileBytes = Convert.FromBase64String(item.Content);
                }
                catch
                {
                    transportFailures.Add($"{itemLabel}: invalid base64 content");
                    continue;
                }
            }
            else
            {
                fileBytes = System.Text.Encoding.UTF8.GetBytes(item.Content);
            }

            uploadRequests.Add(new UploadRequest(
                resolvedId.Value, item.Filename, new MemoryStream(fileBytes),
                null, item.FolderPath, null, null, "MCP"));
        }

        if (uploadRequests.Count == 0)
        {
            var summary = $"Uploaded 0 of {items.Count} file(s).";
            summary += $"\n\nFailures:\n{string.Join("\n", transportFailures.Select(f => $"- {f}"))}";
            return summary;
        }

        var uploadService = services.GetRequiredService<IUploadService>();
        var bulkRequest = new BulkUploadRequest(resolvedId.Value, uploadRequests);
        var result = await uploadService.BulkUploadAsync(bulkRequest, ct);

        // Merge transport failures with service results
        var totalItems = items.Count;
        var succeeded = result.SuccessCount;
        var allFailures = new List<string>(transportFailures);
        foreach (var r in result.Results.Where(r => !r.Success))
            allFailures.Add(r.Error ?? "Unknown error");

        var output = $"Uploaded {succeeded} of {totalItems} file(s) to container '{container!.Name}'.";
        if (allFailures.Count > 0)
            output += $"\n\nFailures:\n{string.Join("\n", allFailures.Select(f => $"- {f}"))}";
        else
            output += "\n\nAll files queued for ingestion (parsing, chunking, embedding).";

        return output;
    }

    [McpServerTool(Name = "get_document", ReadOnly = true, Idempotent = true),
     Description("Retrieve a single document's full text by ID or path. Returns extracted text for binary formats (PDF, DOCX, PPTX). Intended for use after `search_knowledge` returns a `DocumentId` worth reading in entirety, or when the user has named an exact file.")]
    public static async Task<string> GetDocument(
        IServiceProvider services,
        [Description("Container ID or name")] string containerId,
        [Description("Document ID (UUID) or virtual path (e.g., '/docs/readme.md')")] string fileId,
        CancellationToken ct = default)
    {
        var containerStore = services.GetRequiredService<IContainerStore>();
        var resolvedId = await ResolveContainerIdAsync(containerId, containerStore, ct);
        if (resolvedId is null)
            return $"Error: Container '{containerId}' not found.";

        var documentStore = services.GetRequiredService<IDocumentStore>();

        // Support lookup by path or by document ID
        Document? document;
        if (Guid.TryParse(fileId, out _))
        {
            document = await documentStore.GetAsync(fileId, ct);
        }
        else
        {
            var normalizedPath = PathUtilities.NormalizePath(fileId);
            document = await documentStore.GetByPathAsync(resolvedId.Value, normalizedPath, ct);
        }

        if (document is null || document.ContainerId != resolvedId.Value.ToString())
            return $"Error: Document '{fileId}' not found in this container.";

        document.Metadata.TryGetValue("Status", out var status);
        if (status is "Pending" or "Processing" or "Queued")
            return $"Error: Document '{document.FileName}' is still being ingested (status: {status}). Try again later.";

        if (status == "Failed")
        {
            document.Metadata.TryGetValue("ErrorMessage", out var errorMsg);
            return $"Error: Document '{document.FileName}' failed ingestion: {errorMsg ?? "unknown error"}";
        }

        // Read the original file from storage and parse if needed
        var container = await containerStore.GetAsync(resolvedId.Value, ct);
        if (container is null)
            return $"Error: Container '{containerId}' could not be loaded.";

        var managedStorage = services.GetRequiredService<IManagedStorageProvider>();
        var connector = managedStorage.CreateConnector(container.Id);

        string content;
        try
        {
            using var rawStream = await connector.ReadFileAsync(document.Path, ct);

            // Connector streams (MinIO, S3) are non-seekable network streams.
            // Parsers like PdfPig require seekable streams, so buffer into memory first.
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
                if (IsTextNative(extension))
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                    content = await reader.ReadToEndAsync(ct);
                }
                else
                {
                    // Binary format — use a parser to extract text
                    var parsers = services.GetRequiredService<IEnumerable<IDocumentParser>>();
                    var parser = parsers.FirstOrDefault(p => p.SupportedExtensions.Contains(extension));
                    if (parser is null)
                        return $"Error: No parser available for '{extension}' files.";

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
            return $"Error: The backing file for '{document.FileName}' could not be read from storage.";
        }
        finally
        {
            (connector as IDisposable)?.Dispose();
        }

        if (string.IsNullOrWhiteSpace(content))
            return $"Document '{document.FileName}' exists but contains no readable text content.";

        var header = $"Document: {document.FileName}\n" +
                     $"Path: {document.Path}\n" +
                     $"ID: {document.Id}\n" +
                     $"Size: {document.SizeBytes:N0} bytes\n" +
                     $"Created: {document.CreatedAt:u}\n" +
                     $"---\n";

        return header + content;
    }

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".log",
        ".json", ".xml", ".yaml", ".yml"
    };

    private static bool IsTextNative(string extension) => TextExtensions.Contains(extension);

    [McpServerTool(Name = "container_stats", ReadOnly = true, Idempotent = true),
     Description("Get container statistics: document counts, chunk count, storage size, and embedding model info.")]
    public static async Task<string> ContainerStats(
        IServiceProvider services,
        [Description("Container ID or name")] string containerId,
        CancellationToken ct = default)
    {
        var containerStore = services.GetRequiredService<IContainerStore>();
        var resolvedId = await ResolveContainerIdAsync(containerId, containerStore, ct);
        if (resolvedId is null)
            return $"Error: Container '{containerId}' not found.";

        var container = await containerStore.GetAsync(resolvedId.Value, ct);
        if (container is null)
            return $"Error: Container '{containerId}' not found.";

        var documentStore = services.GetRequiredService<IDocumentStore>();
        var stats = await documentStore.GetContainerStatsAsync(resolvedId.Value, ct);

        var modelDiscovery = services.GetRequiredService<VectorModelDiscovery>();
        var models = await modelDiscovery.GetModelsAsync(resolvedId.Value, ct);

        var text = $"Container: {container.Name}\n";

        // Status breakdown only when there are non-ready documents
        if (stats.ProcessingCount > 0 || stats.FailedCount > 0)
            text += $"Documents: {stats.DocumentCount} ({stats.ReadyCount} ready, {stats.ProcessingCount} processing, {stats.FailedCount} failed)\n";
        else
            text += $"Documents: {stats.DocumentCount}\n";

        text += $"Chunks: {stats.TotalChunks:N0}\n";
        text += $"Storage: {FormatBytes(stats.TotalSizeBytes)}\n";

        if (models.Count > 0)
        {
            var primary = models[0];
            text += $"Embedding model: {primary.ModelId} ({primary.Dimensions} dims, {primary.VectorCount:N0} vectors)\n";
        }
        else
        {
            text += "Embedding model: none\n";
        }

        text += stats.LastIndexedAt.HasValue
            ? $"Last indexed: {stats.LastIndexedAt.Value:u}\n"
            : "Last indexed: never\n";
        text += $"Created: {container.CreatedAt:u}";

        return text;
    }

    [McpServerTool(Name = "container_describe", ReadOnly = true, Idempotent = true),
     Description("Returns an agent-optimized description of a knowledge scope — container or source: its description, auto-generated summary (if available), and document statistics. Use this to understand what a scope covers before querying via search_knowledge, or when container_list output is insufficient to choose between them. The response also echoes the server's tool-routing instructions for clients that don't surface them on connect.")]
    public static async Task<string> ContainerDescribe(
        IServiceProvider services,
        [Description("Container or source ID (GUID) or name")] string containerId,
        CancellationToken ct = default)
    {
        var containerStore = services.GetRequiredService<IContainerStore>();
        var sourceStore = services.GetRequiredService<ISourceStore>();

        var owner = await ResolveSearchableOwnerAsync(containerId, containerStore, sourceStore, ct);
        if (owner is null)
            return $"Error: Container '{LogSanitizer.Sanitize(containerId)}' not found.";

        Guid? resolvedId = owner.Id;

        var container = owner.IsSource ? null : await containerStore.GetAsync(owner.Id, ct);
        if (!owner.IsSource && container is null)
            return $"Error: Container '{LogSanitizer.Sanitize(containerId)}' not found.";

        var documentStore = services.GetRequiredService<IDocumentStore>();
        var stats = await documentStore.GetContainerStatsAsync(owner.Id, ct);

        var text = $"{(owner.IsSource ? "Source" : "Container")}: {owner.Name}\n";
        text += $"ID: {owner.Id}\n";
        text += $"Kind: {owner.Kind}\n";

        if (owner.IsSource)
        {
            // Named explicitly so an agent does not waste a call discovering it: a source has
            // no file listing, by design rather than by omission.
            text += "Type: external source (read-only; searchable, but `list_files` does not apply)\n";
        }
        else
        {
            text += "Type: managed storage (browsable and writable)\n";
        }

        if (!string.IsNullOrWhiteSpace(owner.Description))
            text += $"Description: {owner.Description}\n";

        if (!string.IsNullOrWhiteSpace(owner.Summary))
        {
            text += $"Summary: {owner.Summary}\n";
            text += container?.SummaryGeneratedAt is { } generatedAt
                ? $"Summary generated: {generatedAt:u}\n"
                : "";
        }
        else
        {
            text += "Summary: (not yet generated)\n";
        }

        if (stats.ProcessingCount > 0 || stats.FailedCount > 0)
            text += $"Documents: {stats.DocumentCount} ({stats.ReadyCount} ready, {stats.ProcessingCount} processing, {stats.FailedCount} failed)\n";
        else
            text += $"Documents: {stats.DocumentCount}\n";

        text += $"Storage: {FormatBytes(stats.TotalSizeBytes)}\n";
        text += $"Created: {owner.CreatedAt:u}";
        text += "\n\n---\nServer instructions:\n" + McpServerConfig.McpServerInstructions;

        return text;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
    };

    // Helpers

    /// <summary>
    /// Resolves a name or id to a <b>container</b> only.
    /// <para>
    /// Deliberately never resolves a source, and that is load-bearing rather than an
    /// oversight. Every enumerating and mutating tool routes through this method, so a source
    /// id handed to <c>list_files</c>, <c>upload_file</c>, or <c>delete_file</c> resolves to
    /// nothing and the tool reports "not found". Teaching this method about sources would
    /// silently turn <c>list_files</c> into a file-enumeration route over somebody else's S3
    /// bucket — the permissions leak epic #348 exists to close. Tools that legitimately
    /// accept either kind use <see cref="ResolveSearchableOwnerAsync"/> instead.
    /// </para>
    /// </summary>
    private static async Task<Guid?> ResolveContainerIdAsync(string nameOrId, IContainerStore store, CancellationToken ct)
    {
        if (Guid.TryParse(nameOrId, out var guid))
        {
            var container = await store.GetAsync(guid, ct);
            return container is not null ? guid : null;
        }

        var byName = await store.GetByNameAsync(nameOrId.ToLowerInvariant(), ct);
        return byName is not null && Guid.TryParse(byName.Id, out var id) ? id : null;
    }

    /// <summary>
    /// A knowledge scope that can be searched and described: either a managed container or an
    /// external source.
    /// </summary>
    internal sealed record SearchableOwner(
        Guid Id, string Name, string Kind, string? Description, string? Summary,
        int DocumentCount, DateTime CreatedAt)
    {
        public const string ManagedKind = "managed";
        public const string SourceKind = "source";

        public bool IsSource => Kind == SourceKind;
    }

    /// <summary>
    /// Resolves a name or id to either a container or a source.
    /// <para>
    /// Restricted to the tools where accepting both is the whole point: searching a scope and
    /// describing one. Listing a source as a searchable scope — its name, kind and summary —
    /// is not the same as enumerating the documents inside it, and only the former is
    /// permitted. Containers are checked first so an id collision, however improbable between
    /// two GUID spaces, resolves to the more restricted kind.
    /// </para>
    /// </summary>
    private static async Task<SearchableOwner?> ResolveSearchableOwnerAsync(
        string nameOrId, IContainerStore containers, ISourceStore sources, CancellationToken ct)
    {
        if (Guid.TryParse(nameOrId, out var guid))
        {
            var container = await containers.GetAsync(guid, ct);
            if (container is not null)
                return ToOwner(container);

            var source = await sources.GetAsync(guid, ct);
            return source is not null ? ToOwner(source) : null;
        }

        string lowered = nameOrId.ToLowerInvariant();

        var byName = await containers.GetByNameAsync(lowered, ct);
        if (byName is not null)
            return ToOwner(byName);

        var sourceByName = await sources.GetByNameAsync(lowered, ct);
        return sourceByName is not null ? ToOwner(sourceByName) : null;
    }

    private static SearchableOwner? ToOwner(Connapse.Core.Container container) =>
        Guid.TryParse(container.Id, out var id)
            ? new SearchableOwner(id, container.Name, SearchableOwner.ManagedKind,
                container.Description, container.Summary, container.DocumentCount, container.CreatedAt)
            : null;

    private static SearchableOwner ToOwner(Source source) =>
        new(source.Id, source.Name, SearchableOwner.SourceKind,
            source.Description, source.Summary, source.DocumentCount, source.CreatedAt);

    private static string? TruncateToFirstSentence(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return null;

        // Find first sentence-terminating pattern: period/!/? followed by whitespace or end-of-string,
        // where the character before the punctuation is not a digit (avoids "1." "2." list prefixes).
        int sentenceEnd = -1;
        for (int i = 1; i < text.Length; i++)
        {
            char c = text[i];
            if ((c == '.' || c == '!' || c == '?')
                && !char.IsDigit(text[i - 1])
                && (i == text.Length - 1 || char.IsWhiteSpace(text[i + 1])))
            {
                sentenceEnd = i;
                break;
            }
        }

        string firstSentence = sentenceEnd > 0
            ? text[..(sentenceEnd + 1)]
            : text;

        return firstSentence.Length > maxChars
            ? firstSentence[..maxChars] + "…"
            : firstSentence;
    }

}

internal record BulkUploadFileItem
{
    public string? Filename { get; init; }
    public string? Content { get; init; }
    public string? Encoding { get; init; }
    public string? FolderPath { get; init; }
}
