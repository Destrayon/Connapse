using AIKnowledge.Web.Mcp;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace AIKnowledge.Web.Endpoints;

public static class McpEndpoints
{
    public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/mcp").WithTags("MCP");

        // POST /mcp - JSON-RPC 2.0 endpoint
        group.MapPost("/", async (
            [FromBody] McpRequest request,
            [FromServices] McpServer mcpServer,
            CancellationToken ct) =>
        {
            try
            {
                // Validate JSON-RPC version
                if (request.Jsonrpc != "2.0")
                {
                    return Results.Ok(new McpResponse(
                        Jsonrpc: "2.0",
                        Result: null,
                        Error: new McpError(Code: -32600, Message: "Invalid JSON-RPC version"),
                        Id: request.Id));
                }

                object? result = request.Method switch
                {
                    "tools/list" => HandleToolsList(mcpServer),
                    "tools/call" => await HandleToolsCallAsync(mcpServer, request.Params, ct),
                    "ping" => new { status = "ok" },
                    _ => throw new Exception($"Unknown method: {request.Method}")
                };

                return Results.Ok(new McpResponse(
                    Jsonrpc: "2.0",
                    Result: result,
                    Error: null,
                    Id: request.Id));
            }
            catch (Exception ex)
            {
                return Results.Ok(new McpResponse(
                    Jsonrpc: "2.0",
                    Result: null,
                    Error: new McpError(
                        Code: -32603,
                        Message: "Internal error",
                        Data: ex.Message),
                    Id: request.Id));
            }
        })
        .WithName("McpRpc")
        .WithDescription("MCP (Model Context Protocol) JSON-RPC 2.0 endpoint");

        // GET /mcp/tools - List available tools (convenience endpoint)
        group.MapGet("/tools", ([FromServices] McpServer mcpServer) =>
        {
            var tools = mcpServer.ListTools();
            return Results.Ok(new { tools });
        })
        .WithName("ListMcpTools")
        .WithDescription("List all available MCP tools");

        return app;
    }

    private static object HandleToolsList(McpServer mcpServer)
    {
        var tools = mcpServer.ListTools();
        return new { tools };
    }

    private static async Task<object> HandleToolsCallAsync(McpServer mcpServer, object? paramsObj, CancellationToken ct)
    {
        if (paramsObj == null)
            throw new Exception("Parameters are required for tools/call");

        // Deserialize params to McpToolCall
        var json = JsonSerializer.Serialize(paramsObj);
        var toolCall = JsonSerializer.Deserialize<McpToolCall>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (toolCall == null)
            throw new Exception("Invalid tool call parameters");

        var result = await mcpServer.ExecuteToolAsync(toolCall, ct);
        return result;
    }
}
