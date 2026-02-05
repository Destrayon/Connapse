namespace AIKnowledge.Web.Mcp;

/// <summary>
/// MCP (Model Context Protocol) request/response models.
/// </summary>

// JSON-RPC 2.0 Request
public record McpRequest(
    string Jsonrpc,
    string Method,
    object? Params,
    string? Id);

// JSON-RPC 2.0 Response
public record McpResponse(
    string Jsonrpc,
    object? Result,
    McpError? Error,
    string? Id);

public record McpError(
    int Code,
    string Message,
    object? Data = null);

// Tool definitions
public record McpTool(
    string Name,
    string Description,
    McpToolInputSchema InputSchema);

public record McpToolInputSchema(
    string Type,
    Dictionary<string, McpToolProperty> Properties,
    List<string>? Required = null);

public record McpToolProperty(
    string Type,
    string Description,
    object? Default = null,
    List<string>? Enum = null);

// Tool execution
public record McpToolCall(
    string Name,
    Dictionary<string, object>? Arguments = null);

public record McpToolResult(
    List<McpToolContent> Content,
    bool? IsError = null);

public record McpToolContent(
    string Type,
    string Text);
