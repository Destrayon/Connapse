using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Connapse.Web.Problems;

/// <summary>
/// Declares the validation 400 on Identity's resendConfirmationEmail. ASP.NET Core 10.0.14 validates its request
/// and documents the 400 itself; earlier 10.0 patches do neither. The built spec would otherwise depend on which
/// runtime patch the build machine has, and docs/api/openapi.json could never match on both.
/// </summary>
internal sealed class ResendConfirmationValidationTransformer : IOpenApiOperationTransformer
{
    private const string ValidationSchema = nameof(HttpValidationProblemDetails);

    public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (!string.Equals(context.Description.RelativePath, "api/v1/identity/resendConfirmationEmail", StringComparison.OrdinalIgnoreCase))
            return;

        if (context.Document!.Components?.Schemas?.ContainsKey(ValidationSchema) != true)
            context.Document.AddComponent(ValidationSchema, await context.GetOrCreateSchemaAsync(typeof(HttpValidationProblemDetails), null, cancellationToken));

        operation.Responses ??= new OpenApiResponses();
        operation.Responses.TryAdd("400", new OpenApiResponse
        {
            Description = "Bad Request",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/problem+json"] = new() { Schema = new OpenApiSchemaReference(ValidationSchema, context.Document) },
            },
        });
    }
}
