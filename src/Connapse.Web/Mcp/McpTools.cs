using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Vectors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;

namespace Connapse.Web.Mcp;

[McpServerToolType]
public class McpTools
{
    [McpServerTool(Name = "container_create", Destructive = false),
     Description("Create a new container for organizing files. Containers provide isolated vector indexes.")]
    public static async Task<string> ContainerCreate(
        IServiceProvider services,
        [Description("Container name (lowercase alphanumeric and hyphens, 2-128 chars)")] string name,
        [Description("Optional description for the container")] string? description = null,
        CancellationToken ct = default)
    {
        name = name.Trim().ToLowerInvariant();

        if (!PathUtilities.IsValidContainerName(name))
            return "Error: Container name must be 2-128 chars, lowercase alphanumeric and hyphens.";

        var containerStore = services.GetRequiredService<IContainerStore>();

        var existing = await containerStore.GetByNameAsync(name, ct);
        if (existing is not null)
            return $"Error: Container '{name}' already exists.";

        var container = await containerStore.CreateAsync(new CreateContainerRequest(name, description), ct);
        return $"Container '{container.Name}' created.\n\nID: {container.Id}";
    }

    [McpServerTool(Name = "container_list", Destructive = false),
     Description("List all containers with their document counts.")]
    public static async Task<string> ContainerList(
        IServiceProvider services,
        CancellationToken ct = default)
    {
        var containerStore = services.GetRequiredService<IContainerStore>();
        var containers = await containerStore.ListAsync(take: int.MaxValue, ct: ct);

        if (containers.Count == 0)
            return "No containers found.";

        var text = $"Found {containers.Count} container(s):\n\n";
        foreach (var c in containers)
        {
            text += $"- {c.Name} ({c.DocumentCount} files)";
            if (!string.IsNullOrEmpty(c.Description))
                text += $" — {c.Description}";
            text += $"\n  ID: {c.Id}\n";
        }

        return text.TrimEnd();
    }

    [McpServerTool(Name = "container_delete"),
     Description("Delete a container. MinIO containers must be empty first. Filesystem, S3, and AzureBlob containers just stop being indexed — underlying data is not deleted.")]
    public static async Task<string> ContainerDelete(
        IServiceProvider services,
        [Description("Container ID or name")] string containerId,
        CancellationToken ct = default)
    {
        var containerStore = services.GetRequiredService<IContainerStore>();

        var resolvedId = await ResolveContainerIdAsync(containerId, containerStore, ct);
        if (resolvedId is null)
            return $"Error: Container '{containerId}' not found.";

        var deleted = await containerStore.DeleteAsync(resolvedId.Value, ct);
        if (!deleted)
            return $"Error: Container '{containerId}' is not empty. Delete all files first.";

        return $"Container '{containerId}' deleted.";
    }

    [McpServerTool(Name = "search_knowledge", Destructive = false),
     Description("Search within a container using semantic, keyword, or hybrid search. Returns relevant document chunks with scores.")]
    public static async Task<string> SearchKnowledge(
        IServiceProvider services,
        [Description("The search query text")] string query,
        [Description("Container ID or name to search within")] string containerId,
        [Description("Search mode: Semantic (vector), Keyword (full-text), or Hybrid (both). Default: Hybrid")] string? mode = null,
        [Description("Number of results to return. Default: 10")] int? topK = null,
        [Description("Optional: Filter results to a folder subtree (e.g., '/docs/')")] string? path = null,
        [Description("Minimum similarity score floor (0.0-1.0). Defaults to 0.05.")] float? minScore = null,
        CancellationToken ct = default)
    {
        var containerStore = services.GetRequiredService<IContainerStore>();
        var resolvedId = await ResolveContainerIdAsync(containerId, containerStore, ct);
        if (resolvedId is null)
            return $"Error: Container '{containerId}' not found.";

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

        var options = new SearchOptions(
            Mode: parsedMode,
            TopK: effectiveTopK,
            MinScore: effectiveMinScore,
            ContainerId: resolvedId.Value.ToString(),
            Filters: filters);

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

    [McpServerTool(Name = "list_files", Destructive = false),
     Description("List files and folders in a container at a given path.")]
    public static async Task<string> ListFiles(
        IServiceProvider services,
        [Description("Container ID or name")] string containerId,
        [Description("Folder path to list (default: root '/')")] string? path = null,
        CancellationToken ct = default)
    {
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

        var text = $"Contents of {normalizedPath}:\n\n";
        var hasEntries = false;

        foreach (var folderName in folderNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            text += $"[DIR]  {folderName}/\n";
            hasEntries = true;
        }

        foreach (var doc in documents)
        {
            var docParent = PathUtilities.GetParentPath(doc.Path);
            if (!string.Equals(docParent, normalizedPath, StringComparison.OrdinalIgnoreCase))
                continue;

            text += $"[FILE] {doc.FileName} ({doc.SizeBytes:N0} bytes) ID: {doc.Id}\n";
            hasEntries = true;
        }

        if (!hasEntries)
            text += "(empty)\n";

        return text.TrimEnd();
    }

    [McpServerTool(Name = "upload_file"),
     Description("Upload a file to a container. The file will be parsed, chunked, embedded, and made searchable. Provide either 'content' (base64) or 'textContent' (raw text), not both.")]
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

        var parsedStrategy = Enum.TryParse<ChunkingStrategy>(strategy, ignoreCase: true, out var s)
            ? s : ChunkingStrategy.Semantic;

        var destinationPath = path ?? "/";
        var normalizedDest = PathUtilities.NormalizeFolderPath(destinationPath);
        var filePath = PathUtilities.NormalizePath($"{normalizedDest}{fileName}");

        var connectorFactory = services.GetRequiredService<IConnectorFactory>();
        var container = await containerStore.GetAsync(resolvedId.Value, ct);
        var connector = connectorFactory.Create(container!);
        using var stream = new MemoryStream(fileBytes);
        await connector.WriteFileAsync(filePath.TrimStart('/'), stream, ct: ct);

        // Create intermediate folder entries so list_files can discover them
        var folderStore = services.GetRequiredService<IFolderStore>();
        await EnsureIntermediateFoldersAsync(folderStore, resolvedId.Value, normalizedDest, ct);

        var documentId = Guid.NewGuid().ToString();
        var jobId = Guid.NewGuid().ToString();

        var job = new IngestionJob(
            JobId: jobId,
            DocumentId: documentId,
            Path: filePath,
            Options: new IngestionOptions(
                DocumentId: documentId,
                FileName: fileName,
                ContentType: null,
                ContainerId: resolvedId.Value.ToString(),
                Path: filePath,
                Strategy: parsedStrategy,
                Metadata: new Dictionary<string, string>
                {
                    ["OriginalFileName"] = fileName,
                    ["IngestedVia"] = "MCP",
                    ["IngestedAt"] = DateTime.UtcNow.ToString("O")
                }),
            BatchId: null);

        var ingestionQueue = services.GetRequiredService<IIngestionQueue>();
        await ingestionQueue.EnqueueAsync(job, ct);

        return $"File '{fileName}' uploaded to {filePath} and queued for ingestion.\n\n" +
               $"Document ID: {documentId}\nJob ID: {jobId}\n\n" +
               "The file will be parsed, chunked, and embedded in the background.";
    }

    [McpServerTool(Name = "delete_file"),
     Description("Delete a file from a container. This also deletes all associated chunks and vectors.")]
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

        await documentStore.DeleteAsync(fileId, ct);

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

    [McpServerTool(Name = "bulk_delete"),
     Description("Delete multiple files from a container in one operation. Returns per-file results.")]
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

        var containerStore = services.GetRequiredService<IContainerStore>();
        var resolvedId = await ResolveContainerIdAsync(containerId, containerStore, ct);
        if (resolvedId is null)
            return $"Error: Container '{containerId}' not found.";

        var documentStore = services.GetRequiredService<IDocumentStore>();
        var fileSystem = services.GetRequiredService<IKnowledgeFileSystem>();
        var logger = services.GetRequiredService<ILogger<McpTools>>();

        var succeeded = 0;
        var failures = new List<string>();
        var warnings = new List<string>();

        foreach (var fileId in ids)
        {
            var document = await documentStore.GetAsync(fileId, ct);
            if (document is null || document.ContainerId != resolvedId.Value.ToString())
            {
                failures.Add($"{fileId}: not found");
                continue;
            }

            await documentStore.DeleteAsync(fileId, ct);
            succeeded++;

            try
            {
                if (!string.IsNullOrEmpty(document.Path))
                    await fileSystem.DeleteAsync(document.Path, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete backing file {Path}", document.Path);
                warnings.Add($"{fileId} ({document.FileName}): storage cleanup failed");
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
     Description("Upload multiple files to a container in one operation. Each file is parsed, chunked, embedded, and made searchable. Returns per-file results.")]
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
        var connectorFactory = services.GetRequiredService<IConnectorFactory>();
        var connector = connectorFactory.Create(container!);
        var folderStore = services.GetRequiredService<IFolderStore>();
        var ingestionQueue = services.GetRequiredService<IIngestionQueue>();

        var batchId = Guid.NewGuid().ToString();
        var succeeded = 0;
        var failures = new List<string>();

        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var itemLabel = item.Filename ?? $"item[{i}]";

                if (string.IsNullOrWhiteSpace(item.Filename))
                {
                    failures.Add($"{itemLabel}: missing 'filename'");
                    continue;
                }

                if (string.IsNullOrEmpty(item.Content))
                {
                    failures.Add($"{itemLabel}: missing 'content'");
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
                        failures.Add($"{itemLabel}: invalid base64 content");
                        continue;
                    }
                }
                else
                {
                    fileBytes = System.Text.Encoding.UTF8.GetBytes(item.Content);
                }

                var destinationPath = item.FolderPath ?? "/";
                var normalizedDest = PathUtilities.NormalizeFolderPath(destinationPath);
                var filePath = PathUtilities.NormalizePath($"{normalizedDest}{item.Filename}");

                try
                {
                    using var stream = new MemoryStream(fileBytes);
                    await connector.WriteFileAsync(filePath.TrimStart('/'), stream, ct: ct);

                    await EnsureIntermediateFoldersAsync(folderStore, resolvedId.Value, normalizedDest, ct);

                    var documentId = Guid.NewGuid().ToString();
                    var jobId = Guid.NewGuid().ToString();

                    var job = new IngestionJob(
                        JobId: jobId,
                        DocumentId: documentId,
                        Path: filePath,
                        Options: new IngestionOptions(
                            DocumentId: documentId,
                            FileName: item.Filename,
                            ContentType: null,
                            ContainerId: resolvedId.Value.ToString(),
                            Path: filePath,
                            Strategy: ChunkingStrategy.Semantic,
                            Metadata: new Dictionary<string, string>
                            {
                                ["OriginalFileName"] = item.Filename,
                                ["IngestedVia"] = "MCP",
                                ["IngestedAt"] = DateTime.UtcNow.ToString("O")
                            }),
                        BatchId: batchId);

                    await ingestionQueue.EnqueueAsync(job, ct);
                    succeeded++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{itemLabel}: {ex.Message}");
                }
            }
        }
        finally
        {
            (connector as IDisposable)?.Dispose();
        }

        var summary = $"Uploaded {succeeded} of {items.Count} file(s) to container '{container!.Name}'.";
        if (failures.Count > 0)
            summary += $"\n\nFailures:\n{string.Join("\n", failures.Select(f => $"- {f}"))}";
        else
            summary += "\n\nAll files queued for ingestion (parsing, chunking, embedding).";

        return summary;
    }

    [McpServerTool(Name = "get_document", Destructive = false),
     Description("Retrieve the full text content of a document by ID or path. For text files the original content is returned; for binary formats (PDF, DOCX, PPTX) the extracted text is returned.")]
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

        var connectorFactory = services.GetRequiredService<IConnectorFactory>();
        var connector = connectorFactory.Create(container);

        string content;
        try
        {
            using var rawStream = await connector.ReadFileAsync(document.Path, ct);

            // Connector streams (MinIO, S3, AzureBlob) are non-seekable network streams.
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

    [McpServerTool(Name = "container_stats", Destructive = false),
     Description("Get statistics for a container: document counts by status, chunk count, storage size, embedding model, and last indexed time.")]
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
        text += $"Type: {container.ConnectorType}\n";

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

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
    };

    // Helpers
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
    /// Creates all intermediate folder entries for a given destination path.
    /// For example, "/a/b/c/" creates "/a/", "/a/b/", and "/a/b/c/" if they don't exist.
    /// Skips the root path "/".
    /// </summary>
    internal static async Task EnsureIntermediateFoldersAsync(
        IFolderStore folderStore, Guid containerId, string normalizedFolderPath, CancellationToken ct)
    {
        if (normalizedFolderPath == "/")
            return;

        // Split into segments and build up each intermediate path
        var segments = normalizedFolderPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentPath = "/";

        foreach (var segment in segments)
        {
            currentPath += segment + "/";

            if (!await folderStore.ExistsAsync(containerId, currentPath, ct))
            {
                await folderStore.CreateAsync(containerId, currentPath, ct);
            }
        }
    }
}

internal record BulkUploadFileItem
{
    public string? Filename { get; init; }
    public string? Content { get; init; }
    public string? Encoding { get; init; }
    public string? FolderPath { get; init; }
}
