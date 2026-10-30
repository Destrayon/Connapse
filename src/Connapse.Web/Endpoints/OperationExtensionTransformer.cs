using System.Text.Json.Nodes;
using Connapse.Application;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Connapse.Web.Endpoints;

/// <summary>
/// Names, on every route that runs an operation, the operation it runs: its id, the scope a token
/// needs, its risk and whether MCP may reach it. The generic connapse_api_* MCP tools are built from
/// this extension in the committed spec (CON-20).
/// </summary>
public sealed class OperationExtensionTransformer : IOpenApiOperationTransformer
{
    public const string Name = "x-connapse-operation";

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (EndpointOperations.Find(context.Description.ActionDescriptor.EndpointMetadata) is not { } found)
            return Task.CompletedTask;

        operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        operation.Extensions[Name] = new JsonNodeExtension(new JsonObject
        {
            ["id"] = found.Id,
            ["scope"] = found.Scope,
            ["risk"] = found.Risk.ToString(),
            ["mcp"] = found.Mcp.ToString(),
        });
        return Task.CompletedTask;
    }
}
