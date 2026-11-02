using Connapse.Core;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Connapse.Web.Endpoints;

/// <summary>
/// Marks the <c>scopeJson</c> string deprecated in the OpenAPI document (#859): a source's scope is sent as the
/// typed <c>scope</c>, and <c>scopeJson</c> is still accepted for one minor release, then removed.
/// </summary>
internal sealed class DeprecatedScopeJsonTransformer : IOpenApiSchemaTransformer
{
    public const string Description =
        "Deprecated: send the typed \"scope\" instead. Still accepted for one minor release, then removed. Not allowed together with \"scope\".";

    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if ((context.JsonTypeInfo.Type == typeof(CreateSourceApiRequest) || context.JsonTypeInfo.Type == typeof(UpdateSourceRequest))
            && schema.Properties?.TryGetValue("scopeJson", out var property) == true && property is OpenApiSchema scopeJson)
        {
            scopeJson.Deprecated = true;
            scopeJson.Description = Description;
        }
        return Task.CompletedTask;
    }
}
