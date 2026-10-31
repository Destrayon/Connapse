using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Connapse.Web.Problems;

/// <summary>
/// Declares the 401 that Identity's login, refresh and confirmEmail answer (CON-166). MapIdentityApi returns it
/// through a Results&lt;...&gt; union the OpenAPI generator doesn't read, and the API's problem handling writes it
/// as a problem.
/// </summary>
internal sealed class IdentitySignInUnauthorizedTransformer : IOpenApiOperationTransformer
{
    private const string ProblemSchema = nameof(ProblemDetails);

    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["api/v1/identity/login"] =
            "Unauthorized: the email or password is wrong, a two-factor code is missing or wrong, or the account is locked out.",
        ["api/v1/identity/refresh"] =
            "Unauthorized: the refresh token is invalid or expired, or the user's password or security stamp changed since it was issued.",
        ["api/v1/identity/confirmEmail"] =
            "Unauthorized: no user has this id, or the confirmation code is wrong.",
    };

    public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.Description.RelativePath is not { } path || !Descriptions.TryGetValue(path, out string? description))
            return;

        if (context.Document!.Components?.Schemas?.ContainsKey(ProblemSchema) != true)
            context.Document.AddComponent(ProblemSchema, await context.GetOrCreateSchemaAsync(typeof(ProblemDetails), null, cancellationToken));

        operation.Responses ??= new OpenApiResponses();
        operation.Responses.TryAdd("401", new OpenApiResponse
        {
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/problem+json"] = new() { Schema = new OpenApiSchemaReference(ProblemSchema, context.Document) },
            },
        });
    }
}
