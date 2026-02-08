using AIKnowledge.Core;
using AIKnowledge.Core.Interfaces;
using AIKnowledge.Core.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace AIKnowledge.Web.Mcp;

/// <summary>
/// MCP (Model Context Protocol) server that exposes knowledge base as AI tools.
/// </summary>
public class McpServer
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IKnowledgeFileSystem _fileSystem;
    private readonly IIngestionQueue _ingestionQueue;
    private readonly ILogger<McpServer> _logger;

    public McpServer(
        IServiceScopeFactory scopeFactory,
        IKnowledgeFileSystem fileSystem,
        IIngestionQueue ingestionQueue,
        ILogger<McpServer> logger)
    {
        _scopeFactory = scopeFactory;
        _fileSystem = fileSystem;
        _ingestionQueue = ingestionQueue;
        _logger = logger;
    }

    public List<McpTool> ListTools()
    {
        return new List<McpTool>
        {
            new McpTool(
                Name: "container_create",
                Description: "Create a new container for organizing files. Containers provide isolated vector indexes.",
                InputSchema: new McpToolInputSchema(
                    Type: "object",
                    Properties: new Dictionary<string, McpToolProperty>
                    {
                        ["name"] = new McpToolProperty(
                            Type: "string",
                            Description: "Container name (lowercase alphanumeric and hyphens, 2-128 chars)"),
                        ["description"] = new McpToolProperty(
                            Type: "string",
                            Description: "Optional description for the container")
                    },
                    Required: new List<string> { "name" })),

            new McpTool(
                Name: "container_list",
                Description: "List all containers with their document counts.",
                InputSchema: new McpToolInputSchema(
                    Type: "object",
                    Properties: new Dictionary<string, McpToolProperty>())),

            new McpTool(
                Name: "container_delete",
                Description: "Delete an empty container. Fails if the container has files.",
                InputSchema: new McpToolInputSchema(
                    Type: "object",
                    Properties: new Dictionary<string, McpToolProperty>
                    {
                        ["name"] = new McpToolProperty(
                            Type: "string",
                            Description: "Container name to delete")
                    },
                    Required: new List<string> { "name" })),

            new McpTool(
                Name: "search_knowledge",
                Description: "Search within a container using semantic, keyword, or hybrid search. Returns relevant document chunks with scores.",
                InputSchema: new McpToolInputSchema(
                    Type: "object",
                    Properties: new Dictionary<string, McpToolProperty>
                    {
                        ["query"] = new McpToolProperty(
                            Type: "string",
                            Description: "The search query text"),
                        ["containerId"] = new McpToolProperty(
                            Type: "string",
                            Description: "Container ID or name to search within"),
                        ["mode"] = new McpToolProperty(
                            Type: "string",
                            Description: "Search mode: Semantic (vector), Keyword (full-text), or Hybrid (both)",
                            Default: "Hybrid",
                            Enum: new List<string> { "Semantic", "Keyword", "Hybrid" }),
                        ["topK"] = new McpToolProperty(
                            Type: "number",
                            Description: "Number of results to return",
                            Default: 10),
                        ["path"] = new McpToolProperty(
                            Type: "string",
                            Description: "Optional: Filter results to a folder subtree (e.g., '/docs/')"),
                        ["minScore"] = new McpToolProperty(
                            Type: "number",
                            Description: "Minimum similarity score threshold (0.0-1.0). Lower values return more results. Defaults to server setting (typically 0.5).")
                    },
                    Required: new List<string> { "query", "containerId" })),

            new McpTool(
                Name: "list_files",
                Description: "List files and folders in a container at a given path.",
                InputSchema: new McpToolInputSchema(
                    Type: "object",
                    Properties: new Dictionary<string, McpToolProperty>
                    {
                        ["containerId"] = new McpToolProperty(
                            Type: "string",
                            Description: "Container ID or name"),
                        ["path"] = new McpToolProperty(
                            Type: "string",
                            Description: "Folder path to list (default: root '/')")
                    },
                    Required: new List<string> { "containerId" })),

            new McpTool(
                Name: "upload_file",
                Description: "Upload a file to a container. The file will be parsed, chunked, embedded, and made searchable.",
                InputSchema: new McpToolInputSchema(
                    Type: "object",
                    Properties: new Dictionary<string, McpToolProperty>
                    {
                        ["containerId"] = new McpToolProperty(
                            Type: "string",
                            Description: "Container ID or name"),
                        ["content"] = new McpToolProperty(
                            Type: "string",
                            Description: "Base64-encoded file content"),
                        ["fileName"] = new McpToolProperty(
                            Type: "string",
                            Description: "Original file name with extension"),
                        ["path"] = new McpToolProperty(
                            Type: "string",
                            Description: "Destination folder path (e.g., '/docs/2026/')"),
                        ["strategy"] = new McpToolProperty(
                            Type: "string",
                            Description: "Chunking strategy",
                            Default: "Semantic",
                            Enum: new List<string> { "Semantic", "FixedSize", "Recursive" })
                    },
                    Required: new List<string> { "containerId", "content", "fileName" })),

            new McpTool(
                Name: "delete_file",
                Description: "Delete a file from a container. This also deletes all associated chunks and vectors.",
                InputSchema: new McpToolInputSchema(
                    Type: "object",
                    Properties: new Dictionary<string, McpToolProperty>
                    {
                        ["containerId"] = new McpToolProperty(
                            Type: "string",
                            Description: "Container ID or name"),
                        ["fileId"] = new McpToolProperty(
                            Type: "string",
                            Description: "File (document) ID to delete")
                    },
                    Required: new List<string> { "containerId", "fileId" }))
        };
    }

    public async Task<McpToolResult> ExecuteToolAsync(McpToolCall toolCall, CancellationToken ct = default)
    {
        try
        {
            return toolCall.Name switch
            {
                "container_create" => await ExecuteContainerCreateAsync(toolCall.Arguments, ct),
                "container_list" => await ExecuteContainerListAsync(ct),
                "container_delete" => await ExecuteContainerDeleteAsync(toolCall.Arguments, ct),
                "search_knowledge" => await ExecuteSearchKnowledgeAsync(toolCall.Arguments, ct),
                "list_files" => await ExecuteListFilesAsync(toolCall.Arguments, ct),
                "upload_file" => await ExecuteUploadFileAsync(toolCall.Arguments, ct),
                "delete_file" => await ExecuteDeleteFileAsync(toolCall.Arguments, ct),
                _ => ErrorResult($"Unknown tool: {toolCall.Name}")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing MCP tool {ToolName}", toolCall.Name);
            return ErrorResult($"Error: {ex.Message}");
        }
    }

    private async Task<McpToolResult> ExecuteContainerCreateAsync(Dictionary<string, object>? args, CancellationToken ct)
    {
        if (args is null || !args.TryGetValue("name", out var nameObj))
            return ErrorResult("'name' parameter is required.");

        var name = nameObj.ToString()!.Trim().ToLowerInvariant();
        var description = args.TryGetValue("description", out var descObj) ? descObj.ToString() : null;

        if (!PathUtilities.IsValidContainerName(name))
            return ErrorResult("Container name must be 2-128 chars, lowercase alphanumeric and hyphens.");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var containerStore = scope.ServiceProvider.GetRequiredService<IContainerStore>();

        var existing = await containerStore.GetByNameAsync(name, ct);
        if (existing is not null)
            return ErrorResult($"Container '{name}' already exists.");

        var container = await containerStore.CreateAsync(new CreateContainerRequest(name, description), ct);

        return TextResult($"Container '{container.Name}' created.\n\nID: {container.Id}");
    }

    private async Task<McpToolResult> ExecuteContainerListAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var containerStore = scope.ServiceProvider.GetRequiredService<IContainerStore>();

        var containers = await containerStore.ListAsync(ct);

        if (containers.Count == 0)
            return TextResult("No containers found.");

        var text = $"Found {containers.Count} container(s):\n\n";
        foreach (var c in containers)
        {
            text += $"- {c.Name} ({c.DocumentCount} files)";
            if (!string.IsNullOrEmpty(c.Description))
                text += $" — {c.Description}";
            text += $"\n  ID: {c.Id}\n";
        }

        return TextResult(text.TrimEnd());
    }

    private async Task<McpToolResult> ExecuteContainerDeleteAsync(Dictionary<string, object>? args, CancellationToken ct)
    {
        if (args is null || !args.TryGetValue("name", out var nameObj))
            return ErrorResult("'name' parameter is required.");

        var name = nameObj.ToString()!;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var containerStore = scope.ServiceProvider.GetRequiredService<IContainerStore>();

        var containerId = await ResolveContainerIdAsync(name, containerStore, ct);
        if (containerId is null)
            return ErrorResult($"Container '{name}' not found.");

        var deleted = await containerStore.DeleteAsync(containerId.Value, ct);
        if (!deleted)
            return ErrorResult($"Container '{name}' is not empty. Delete all files first.");

        return TextResult($"Container '{name}' deleted.");
    }

    private async Task<McpToolResult> ExecuteSearchKnowledgeAsync(Dictionary<string, object>? args, CancellationToken ct)
    {
        if (args is null || !args.TryGetValue("query", out var queryObj) || !args.TryGetValue("containerId", out var contObj))
            return ErrorResult("'query' and 'containerId' parameters are required.");

        var query = queryObj.ToString()!;
        var mode = args.TryGetValue("mode", out var modeObj) && Enum.TryParse<SearchMode>(modeObj.ToString(), out var parsedMode)
            ? parsedMode
            : SearchMode.Hybrid;
        var topK = args.TryGetValue("topK", out var topKObj) ? Convert.ToInt32(topKObj) : 10;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var containerStore = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        var containerId = await ResolveContainerIdAsync(contObj.ToString()!, containerStore, ct);
        if (containerId is null)
            return ErrorResult($"Container '{contObj}' not found.");

        // Resolve minScore: use caller value, fall back to configured setting
        float effectiveMinScore;
        if (args.TryGetValue("minScore", out var minScoreObj))
        {
            effectiveMinScore = Convert.ToSingle(minScoreObj);
        }
        else
        {
            var searchSettings = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<SearchSettings>>();
            effectiveMinScore = (float)searchSettings.CurrentValue.MinimumScore;
        }

        var options = new SearchOptions(
            Mode: mode,
            TopK: topK,
            MinScore: effectiveMinScore,
            ContainerId: containerId.Value.ToString());

        var searchService = scope.ServiceProvider.GetRequiredService<IKnowledgeSearch>();
        var result = await searchService.SearchAsync(query, options, ct);

        var resultText = $"Found {result.TotalMatches} results in {result.Duration.TotalMilliseconds:F0}ms:\n\n";

        foreach (var hit in result.Hits)
        {
            resultText += $"Score: {hit.Score:F3}\n";
            resultText += $"Content: {hit.Content}\n";
            if (hit.Metadata.TryGetValue("FileName", out var fileName))
                resultText += $"File: {fileName}\n";
            resultText += "\n";
        }

        return TextResult(resultText.TrimEnd());
    }

    private async Task<McpToolResult> ExecuteListFilesAsync(Dictionary<string, object>? args, CancellationToken ct)
    {
        if (args is null || !args.TryGetValue("containerId", out var contObj))
            return ErrorResult("'containerId' parameter is required.");

        var folderPath = args.TryGetValue("path", out var pathObj) ? pathObj.ToString()! : "/";

        await using var scope = _scopeFactory.CreateAsyncScope();
        var containerStore = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        var containerId = await ResolveContainerIdAsync(contObj.ToString()!, containerStore, ct);
        if (containerId is null)
            return ErrorResult($"Container '{contObj}' not found.");

        var documentStore = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var folderStore = scope.ServiceProvider.GetRequiredService<IFolderStore>();

        var normalizedPath = PathUtilities.NormalizeFolderPath(folderPath);

        // Get folders
        var folders = await folderStore.ListAsync(containerId.Value, parentPath: normalizedPath, ct);
        // Get documents
        var documents = await documentStore.ListAsync(containerId.Value, pathPrefix: normalizedPath, ct: ct);

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

        return TextResult(text.TrimEnd());
    }

    private async Task<McpToolResult> ExecuteUploadFileAsync(Dictionary<string, object>? args, CancellationToken ct)
    {
        if (args is null ||
            !args.TryGetValue("containerId", out var contObj) ||
            !args.TryGetValue("content", out var contentObj) ||
            !args.TryGetValue("fileName", out var fileNameObj))
            return ErrorResult("'containerId', 'content', and 'fileName' parameters are required.");

        var fileName = fileNameObj.ToString()!;
        var base64Content = contentObj.ToString()!;
        var destinationPath = args.TryGetValue("path", out var pathObj) ? pathObj.ToString()! : "/";
        var strategyStr = args.TryGetValue("strategy", out var stratObj) ? stratObj.ToString() : "Semantic";

        if (!Enum.TryParse<ChunkingStrategy>(strategyStr, out var strategy))
            strategy = ChunkingStrategy.Semantic;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var containerStore = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        var containerId = await ResolveContainerIdAsync(contObj.ToString()!, containerStore, ct);
        if (containerId is null)
            return ErrorResult($"Container '{contObj}' not found.");

        byte[] fileBytes;
        try
        {
            fileBytes = Convert.FromBase64String(base64Content);
        }
        catch
        {
            return ErrorResult("'content' must be valid base64-encoded data.");
        }

        var normalizedDest = PathUtilities.NormalizeFolderPath(destinationPath);
        var filePath = PathUtilities.NormalizePath($"{normalizedDest}{fileName}");

        using var stream = new MemoryStream(fileBytes);
        await _fileSystem.SaveFileAsync(filePath, stream, ct);

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
                ContainerId: containerId.Value.ToString(),
                Path: filePath,
                Strategy: strategy,
                Metadata: new Dictionary<string, string>
                {
                    ["OriginalFileName"] = fileName,
                    ["IngestedVia"] = "MCP",
                    ["IngestedAt"] = DateTime.UtcNow.ToString("O")
                }),
            BatchId: null);

        await _ingestionQueue.EnqueueAsync(job, ct);

        return TextResult(
            $"File '{fileName}' uploaded to {filePath} and queued for ingestion.\n\n" +
            $"Document ID: {documentId}\nJob ID: {jobId}\n\n" +
            "The file will be parsed, chunked, and embedded in the background.");
    }

    private async Task<McpToolResult> ExecuteDeleteFileAsync(Dictionary<string, object>? args, CancellationToken ct)
    {
        if (args is null ||
            !args.TryGetValue("containerId", out var contObj) ||
            !args.TryGetValue("fileId", out var fileIdObj))
            return ErrorResult("'containerId' and 'fileId' parameters are required.");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var containerStore = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        var containerId = await ResolveContainerIdAsync(contObj.ToString()!, containerStore, ct);
        if (containerId is null)
            return ErrorResult($"Container '{contObj}' not found.");

        var documentStore = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var fileId = fileIdObj.ToString()!;
        var document = await documentStore.GetAsync(fileId, ct);

        if (document is null || document.ContainerId != containerId.Value.ToString())
            return ErrorResult($"File '{fileId}' not found in this container.");

        await documentStore.DeleteAsync(fileId, ct);

        try
        {
            if (!string.IsNullOrEmpty(document.Path))
                await _fileSystem.DeleteAsync(document.Path, ct);
        }
        catch { }

        return TextResult($"File '{document.FileName}' (ID: {fileId}) deleted.");
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

    private static McpToolResult TextResult(string text) => new(
        Content: new List<McpToolContent> { new(Type: "text", Text: text) });

    private static McpToolResult ErrorResult(string text) => new(
        Content: new List<McpToolContent> { new(Type: "text", Text: $"Error: {text}") },
        IsError: true);
}
