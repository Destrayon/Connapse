using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Connapse.Web.Mcp;

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
                Name: "search_knowledge",
                Description: "Search the knowledge base using semantic, keyword, or hybrid search. Returns relevant document chunks with scores.",
                InputSchema: new McpToolInputSchema(
                    Type: "object",
                    Properties: new Dictionary<string, McpToolProperty>
                    {
                        ["query"] = new McpToolProperty(
                            Type: "string",
                            Description: "The search query text"),
                        ["mode"] = new McpToolProperty(
                            Type: "string",
                            Description: "Search mode: Semantic (vector), Keyword (full-text), or Hybrid (both)",
                            Default: "Hybrid",
                            Enum: new List<string> { "Semantic", "Keyword", "Hybrid" }),
                        ["topK"] = new McpToolProperty(
                            Type: "number",
                            Description: "Number of results to return",
                            Default: 10),
                        ["collectionId"] = new McpToolProperty(
                            Type: "string",
                            Description: "Optional: Filter results to a specific collection")
                    },
                    Required: new List<string> { "query" })),

            new McpTool(
                Name: "list_documents",
                Description: "List all documents in the knowledge base. Optionally filter by collection ID.",
                InputSchema: new McpToolInputSchema(
                    Type: "object",
                    Properties: new Dictionary<string, McpToolProperty>
                    {
                        ["collectionId"] = new McpToolProperty(
                            Type: "string",
                            Description: "Optional: Filter documents by collection ID")
                    })),

            new McpTool(
                Name: "ingest_document",
                Description: "Add a document to the knowledge base from a URL or file path. The document will be parsed, chunked, embedded, and made searchable.",
                InputSchema: new McpToolInputSchema(
                    Type: "object",
                    Properties: new Dictionary<string, McpToolProperty>
                    {
                        ["path"] = new McpToolProperty(
                            Type: "string",
                            Description: "Virtual path for the document in the knowledge base (e.g., '/documents/report.pdf')"),
                        ["content"] = new McpToolProperty(
                            Type: "string",
                            Description: "Base64-encoded document content"),
                        ["fileName"] = new McpToolProperty(
                            Type: "string",
                            Description: "Original file name with extension"),
                        ["collectionId"] = new McpToolProperty(
                            Type: "string",
                            Description: "Optional: Collection ID to organize related documents"),
                        ["strategy"] = new McpToolProperty(
                            Type: "string",
                            Description: "Chunking strategy",
                            Default: "Semantic",
                            Enum: new List<string> { "Semantic", "FixedSize", "Recursive" })
                    },
                    Required: new List<string> { "path", "content", "fileName" }))
        };
    }

    public async Task<McpToolResult> ExecuteToolAsync(McpToolCall toolCall, CancellationToken ct = default)
    {
        try
        {
            return toolCall.Name switch
            {
                "search_knowledge" => await ExecuteSearchKnowledgeAsync(toolCall.Arguments, ct),
                "list_documents" => await ExecuteListDocumentsAsync(toolCall.Arguments, ct),
                "ingest_document" => await ExecuteIngestDocumentAsync(toolCall.Arguments, ct),
                _ => new McpToolResult(
                    Content: new List<McpToolContent>
                    {
                        new McpToolContent(Type: "text", Text: $"Unknown tool: {toolCall.Name}")
                    },
                    IsError: true)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing MCP tool {ToolName}", toolCall.Name);
            return new McpToolResult(
                Content: new List<McpToolContent>
                {
                    new McpToolContent(Type: "text", Text: $"Error: {ex.Message}")
                },
                IsError: true);
        }
    }

    private async Task<McpToolResult> ExecuteSearchKnowledgeAsync(Dictionary<string, object>? args, CancellationToken ct)
    {
        if (args == null || !args.TryGetValue("query", out var queryObj))
        {
            return new McpToolResult(
                Content: new List<McpToolContent>
                {
                    new McpToolContent(Type: "text", Text: "Error: 'query' parameter is required")
                },
                IsError: true);
        }

        var query = queryObj.ToString()!;
        var mode = args.TryGetValue("mode", out var modeObj) && Enum.TryParse<SearchMode>(modeObj.ToString(), out var parsedMode)
            ? parsedMode
            : SearchMode.Hybrid;
        var topK = args.TryGetValue("topK", out var topKObj) ? Convert.ToInt32(topKObj) : 10;
        var collectionId = args.TryGetValue("collectionId", out var collObj) ? collObj.ToString() : null;

        var options = new SearchOptions(
            Mode: mode,
            TopK: topK,
            CollectionId: collectionId);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var searchService = scope.ServiceProvider.GetRequiredService<IKnowledgeSearch>();
        var result = await searchService.SearchAsync(query, options, ct);

        var resultText = $"Found {result.TotalMatches} results in {result.Duration.TotalMilliseconds:F0}ms:\n\n";

        foreach (var hit in result.Hits)
        {
            resultText += $"Score: {hit.Score:F3}\n";
            resultText += $"Content: {hit.Content}\n";
            if (hit.Metadata.TryGetValue("FileName", out var fileName))
                resultText += $"File: {fileName}\n";
            if (hit.Metadata.TryGetValue("source", out var source))
                resultText += $"Source: {source}\n";
            resultText += "\n";
        }

        return new McpToolResult(
            Content: new List<McpToolContent>
            {
                new McpToolContent(Type: "text", Text: resultText.TrimEnd())
            });
    }

    private async Task<McpToolResult> ExecuteListDocumentsAsync(Dictionary<string, object>? args, CancellationToken ct)
    {
        var collectionId = args?.TryGetValue("collectionId", out var collObj) == true ? collObj.ToString() : null;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var documentStore = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var documents = await documentStore.ListAsync(collectionId, ct);

        var resultText = $"Found {documents.Count} documents:\n\n";

        foreach (var doc in documents)
        {
            resultText += $"ID: {doc.Id}\n";
            resultText += $"File: {doc.FileName}\n";
            resultText += $"Size: {doc.SizeBytes:N0} bytes\n";
            resultText += $"Created: {doc.CreatedAt:yyyy-MM-dd HH:mm:ss}\n";
            if (!string.IsNullOrEmpty(doc.CollectionId))
                resultText += $"Collection: {doc.CollectionId}\n";
            resultText += "\n";
        }

        return new McpToolResult(
            Content: new List<McpToolContent>
            {
                new McpToolContent(Type: "text", Text: resultText.TrimEnd())
            });
    }

    private async Task<McpToolResult> ExecuteIngestDocumentAsync(Dictionary<string, object>? args, CancellationToken ct)
    {
        if (args == null)
        {
            return new McpToolResult(
                Content: new List<McpToolContent>
                {
                    new McpToolContent(Type: "text", Text: "Error: Arguments are required")
                },
                IsError: true);
        }

        if (!args.TryGetValue("path", out var pathObj) ||
            !args.TryGetValue("content", out var contentObj) ||
            !args.TryGetValue("fileName", out var fileNameObj))
        {
            return new McpToolResult(
                Content: new List<McpToolContent>
                {
                    new McpToolContent(Type: "text", Text: "Error: 'path', 'content', and 'fileName' parameters are required")
                },
                IsError: true);
        }

        var virtualPath = pathObj.ToString()!;
        var base64Content = contentObj.ToString()!;
        var fileName = fileNameObj.ToString()!;
        var collectionId = args.TryGetValue("collectionId", out var collObj) ? collObj.ToString() : null;
        var strategyStr = args.TryGetValue("strategy", out var stratObj) ? stratObj.ToString() : "Semantic";

        if (!Enum.TryParse<ChunkingStrategy>(strategyStr, out var strategy))
            strategy = ChunkingStrategy.Semantic;

        // Decode base64 content
        byte[] fileBytes;
        try
        {
            fileBytes = Convert.FromBase64String(base64Content);
        }
        catch
        {
            return new McpToolResult(
                Content: new List<McpToolContent>
                {
                    new McpToolContent(Type: "text", Text: "Error: 'content' must be valid base64-encoded data")
                },
                IsError: true);
        }

        // Save file to storage
        using var stream = new MemoryStream(fileBytes);
        await _fileSystem.SaveFileAsync(virtualPath, stream, ct);

        // Enqueue ingestion job
        var documentId = Guid.NewGuid().ToString();
        var jobId = Guid.NewGuid().ToString();

        var job = new IngestionJob(
            JobId: jobId,
            DocumentId: documentId,
            VirtualPath: virtualPath,
            Options: new IngestionOptions(
                DocumentId: documentId,
                FileName: fileName,
                ContentType: null,
                CollectionId: collectionId,
                Strategy: strategy,
                Metadata: new Dictionary<string, string>
                {
                    ["OriginalFileName"] = fileName,
                    ["IngestedVia"] = "MCP",
                    ["IngestedAt"] = DateTime.UtcNow.ToString("O")
                }),
            BatchId: null);

        await _ingestionQueue.EnqueueAsync(job, ct);

        return new McpToolResult(
            Content: new List<McpToolContent>
            {
                new McpToolContent(
                    Type: "text",
                    Text: $"Document '{fileName}' successfully uploaded and queued for ingestion.\n\nDocument ID: {documentId}\nJob ID: {jobId}\n\nThe document will be parsed, chunked, and embedded in the background.")
            });
    }
}
