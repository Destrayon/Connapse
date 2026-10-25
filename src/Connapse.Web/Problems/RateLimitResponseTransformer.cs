using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi;

namespace Connapse.Web.Problems;

/// <summary>
/// Declares the rate limiter's 429 on every rate-limited /api operation. The limiter answers before the handler
/// runs, as the problem <see cref="RateLimitingExtensions.WriteRejectionAsync"/> writes, so no handler declares it.
/// Outside /api the OAuth endpoints keep their protocol's error shape and are left alone.
/// </summary>
internal sealed class RateLimitResponseTransformer : IOpenApiOperationTransformer
{
    private const string ProblemSchema = nameof(ProblemDetails);

    public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        bool limited = context.Description.ActionDescriptor.EndpointMetadata.OfType<EnableRateLimitingAttribute>().Any();
        if (!limited || context.Description.RelativePath?.StartsWith("api/", StringComparison.OrdinalIgnoreCase) != true)
            return;

        if (context.Document!.Components?.Schemas?.ContainsKey(ProblemSchema) != true)
            context.Document.AddComponent(ProblemSchema, await context.GetOrCreateSchemaAsync(typeof(ProblemDetails), null, cancellationToken));

        operation.Responses ??= new OpenApiResponses();
        operation.Responses.TryAdd("429", new OpenApiResponse
        {
            Description = "Too Many Requests: the rate limit for this caller is used up. Retry-After, when present, says when to try again.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/problem+json"] = new() { Schema = new OpenApiSchemaReference(ProblemSchema, context.Document) },
            },
        });
    }
}
