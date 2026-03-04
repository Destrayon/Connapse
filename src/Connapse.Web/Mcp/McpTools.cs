using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using System.ComponentModel;

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
        var containers = await containerStore.ListAsync(ct);

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
     Description("Delete a container. MinIO and InMemory containers must be empty first. Filesystem, S3, and AzureBlob containers just stop being indexed — underlying data is not deleted.")]
    public static async Task<string> ContainerDelete(
        IServiceProvider services,
        [Description("Container name or ID to delete")] string name,
        CancellationToken ct = default)
    {
        var containerStore = services.GetRequiredService<IContainerStore>();

        var containerId = await ResolveContainerIdAsync(name, containerStore, ct);
        if (containerId is null)
            return $"Error: Container '{name}' not found.";

        var deleted = await containerStore.DeleteAsync(containerId.Value, ct);
        if (!deleted)
            return $"Error: Container '{name}' is not empty. Delete all files first.";

        return $"Container '{name}' deleted.";
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

        var options = new SearchOptions(
            Mode: parsedMode,
            TopK: effectiveTopK,
            MinScore: effectiveMinScore,
            ContainerId: resolvedId.Value.ToString());

        var searchService = services.GetRequiredService<IKnowledgeSearch>();
        var result = await searchService.SearchAsync(query, options, ct);

        var resultText = $"Found {result.TotalMatches} results in {result.Duration.TotalMilliseconds:F0}ms:\n\n";
        foreach (var hit in result.Hits)
        {
            resultText += $"Content: {hit.Content}\n";
            if (hit.Metadata.TryGetValue("FileName", out var fileName))
                resultText += $"File: {fileName}\n";
            resultText += "\n";
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

        var folders = await folderStore.ListAsync(resolvedId.Value, parentPath: normalizedPath, ct);
        var documents = await documentStore.ListAsync(resolvedId.Value, pathPrefix: normalizedPath, ct: ct);

        var text = $"Contents of {normalizedPath}:\n\n";
        var hasEntries = false;

        foreach (var folder in folders)
        {
            var folderName = PathUtilities.GetFileName(folder.Path.TrimEnd('/'));
            text += $"[DIR]  {folderName}/\n";
            hasEntries = true;
        }

        foreach (var doc in documents)
        {
            var docParent = PathUtilities.GetParentPath(doc.Path);
            if (!string.Equals(docParent, normalizedPath, StringComparison.OrdinalIgnoreCase))
                continue;

            text += $"[FILE] {doc.FileName} ({doc.SizeBytes:N0} bytes)\n";
            hasEntries = true;
        }

        if (!hasEntries)
            text += "(empty)\n";

        return text.TrimEnd();
    }

    [McpServerTool(Name = "upload_file"),
     Description("Upload a file to a container. The file will be parsed, chunked, embedded, and made searchable.")]
    public static async Task<string> UploadFile(
        IServiceProvider services,
        [Description("Container ID or name")] string containerId,
        [Description("Base64-encoded file content")] string content,
        [Description("Original file name with extension")] string fileName,
        [Description("Destination folder path (e.g., '/docs/2026/')")] string? path = null,
        [Description("Chunking strategy: Semantic, FixedSize, or Recursive. Default: Semantic")] string? strategy = null,
        CancellationToken ct = default)
    {
        var containerStore = services.GetRequiredService<IContainerStore>();
        var resolvedId = await ResolveContainerIdAsync(containerId, containerStore, ct);
        if (resolvedId is null)
            return $"Error: Container '{containerId}' not found.";

        byte[] fileBytes;
        try
        {
            fileBytes = Convert.FromBase64String(content);
        }
        catch
        {
            return "Error: 'content' must be valid base64-encoded data.";
        }

        var parsedStrategy = Enum.TryParse<ChunkingStrategy>(strategy, ignoreCase: true, out var s)
            ? s : ChunkingStrategy.Semantic;

        var destinationPath = path ?? "/";
        var normalizedDest = PathUtilities.NormalizeFolderPath(destinationPath);
        var filePath = PathUtilities.NormalizePath($"{normalizedDest}{fileName}");

        var fileSystem = services.GetRequiredService<IKnowledgeFileSystem>();
        using var stream = new MemoryStream(fileBytes);
        await fileSystem.SaveFileAsync(filePath, stream, ct);

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

        try
        {
            var fileSystem = services.GetRequiredService<IKnowledgeFileSystem>();
            if (!string.IsNullOrEmpty(document.Path))
                await fileSystem.DeleteAsync(document.Path, ct);
        }
        catch { }

        return $"File '{document.FileName}' (ID: {fileId}) deleted.";
    }

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
}
