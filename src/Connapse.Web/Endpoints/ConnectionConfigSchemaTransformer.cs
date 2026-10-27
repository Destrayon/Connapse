using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Connapse.Web.Endpoints;

/// <summary>
/// Documents a connection's <c>config</c> as a JSON object or null (CON-11). Its type is a nullable JsonElement,
/// which the generator writes as oneOf null and any JSON: a null matches both, so oneOf rejects it.
/// </summary>
internal sealed class ConnectionConfigSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Type == typeof(ConnectionResponse) && schema.Properties?.ContainsKey("config") == true)
            schema.Properties["config"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object | JsonSchemaType.Null,
                AdditionalProperties = new OpenApiSchema(),
                Description = "The provider's settings; null when the connection has none.",
            };
        return Task.CompletedTask;
    }
}
