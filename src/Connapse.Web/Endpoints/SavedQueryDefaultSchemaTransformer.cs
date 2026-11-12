using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Connapse.Web.Endpoints;

/// <summary>
/// Documents a saved-query parameter's <c>default</c> as any JSON value or null (CON-311). Its type is a nullable
/// JsonElement, which the generator writes as oneOf null and any JSON: a null matches both, so oneOf rejects it.
/// </summary>
internal sealed class SavedQueryDefaultSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        Type type = context.JsonTypeInfo.Type;
        if ((type == typeof(SavedQueryParameterResponse) || type == typeof(SaveTableQueryParameter))
            && schema.Properties?.ContainsKey("default") == true)
            schema.Properties["default"] = new OpenApiSchema
            {
                Description = "The value used when the caller leaves the parameter out, as JSON of the parameter's type; null when it has none.",
            };
        return Task.CompletedTask;
    }
}
