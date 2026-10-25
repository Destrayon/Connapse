using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Connapse.Web.Endpoints;

/// <summary>
/// Writes a <c>multipart/form-data</c> request body as one object with a property per form part (CON-81). The generator
/// writes each <c>[FromForm]</c> parameter as its own object inside an <c>allOf</c>, which Kiota turns into a model
/// whose multipart writer throws "Expected a MultiPartBody instance", so a generated client couldn't upload. As one
/// object, the files part a binary array, the body is Kiota's <c>MultipartBody</c>: a part per field, a file with
/// its name. The parts the server reads are the same.
/// </summary>
internal sealed class MultipartFormTransformer : IOpenApiOperationTransformer
{
    private const string Multipart = "multipart/form-data";

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (operation.RequestBody is not OpenApiRequestBody { Content: { } content }
            || !content.TryGetValue(Multipart, out var media)
            || media.Schema is not OpenApiSchema { AllOf: { Count: > 0 } parts } form
            || parts.Any(part => part is not OpenApiSchema { Properties.Count: > 0 }))
            return Task.CompletedTask;

        form.Properties ??= new Dictionary<string, IOpenApiSchema>();
        foreach (var part in parts.Cast<OpenApiSchema>())
        {
            foreach (var (name, schema) in part.Properties!)
                form.Properties[name] = schema;
            foreach (var name in part.Required ?? new HashSet<string>())
                (form.Required ??= new HashSet<string>()).Add(name);
        }
        form.AllOf = null;
        form.Type = JsonSchemaType.Object;
        return Task.CompletedTask;
    }
}
