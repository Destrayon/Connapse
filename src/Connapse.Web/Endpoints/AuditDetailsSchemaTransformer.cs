using Connapse.Core;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Connapse.Web.Endpoints;

/// <summary>
/// Documents an audit entry's <c>details</c> as a JSON object or null (#880). Its type is a nullable
/// JsonElement, which the generator writes as oneOf null and any JSON: a null matches both, so oneOf rejects it.
/// </summary>
internal sealed class AuditDetailsSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Type == typeof(AuditEntry) && schema.Properties?.ContainsKey("details") == true)
            schema.Properties["details"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object | JsonSchemaType.Null,
                AdditionalProperties = new OpenApiSchema(),
                Description = "What the action changed, as the action records it; null when it records nothing.",
            };
        return Task.CompletedTask;
    }
}
