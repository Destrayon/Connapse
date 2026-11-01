using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Connapse.Web.Problems;

/// <summary>
/// Declares the members every API problem carries beyond RFC 9457's: <c>error</c>, which
/// <see cref="ProblemDetailsExtensions"/> adds to each one, and <c>traceId</c>; and those some add:
/// <c>message</c>, <c>field</c>, <c>troubleshootingUrl</c>, and an <c>errors</c> list. Without them a client
/// generated from the spec can't read why a request failed.
/// </summary>
internal sealed class ProblemDetailsSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Type != typeof(ProblemDetails))
            return Task.CompletedTask;

        schema.Properties ??= new Dictionary<string, IOpenApiSchema>();
        schema.Properties["error"] = Text("A machine-readable code, or the error's text when there is no code.");
        schema.Properties["message"] = Text("The human-readable text, when error is a code.");
        schema.Properties["field"] = Text("The request member at fault, when there is one.");
        // A list of reasons from a handler, or each field's messages from request validation.
        var reasons = new OpenApiSchema { Type = JsonSchemaType.Array, Items = new OpenApiSchema { Type = JsonSchemaType.String } };
        schema.Properties["errors"] = new OpenApiSchema
        {
            AnyOf = [reasons, new OpenApiSchema { Type = JsonSchemaType.Object, AdditionalProperties = reasons }],
            Description = "Every reason, when a request failed for several: a list, or each request field's messages.",
        };
        schema.Properties["troubleshootingUrl"] = Text("The setup guide's entry for this failure, when there is one.");
        schema.Properties["traceId"] = Text("The request's trace, to find it in the server's log.");
        schema.Required ??= new HashSet<string>();
        schema.Required.Add("error");
        return Task.CompletedTask;
    }

    private static OpenApiSchema Text(string description) => new() { Type = JsonSchemaType.String, Description = description };
}
