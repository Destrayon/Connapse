using System.Reflection;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Connapse.Web.Endpoints;

/// <summary>
/// Marks each property with <see cref="ObsoleteAttribute"/> deprecated in the OpenAPI document, its description the
/// attribute's message: a property still accepted for old clients stays in the API's shape rather than vanishing from it.
/// </summary>
internal sealed class ObsoletePropertySchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (schema.Properties is null)
            return Task.CompletedTask;
        foreach (var info in context.JsonTypeInfo.Properties)
        {
            if (info.AttributeProvider is MemberInfo member && member.GetCustomAttribute<ObsoleteAttribute>() is { } obsolete
                && schema.Properties.TryGetValue(info.Name, out var property) && property is OpenApiSchema deprecated)
            {
                deprecated.Deprecated = true;
                deprecated.Description = obsolete.Message;
            }
        }
        return Task.CompletedTask;
    }
}
