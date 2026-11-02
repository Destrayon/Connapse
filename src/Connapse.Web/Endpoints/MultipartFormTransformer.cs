using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Connapse.Web.Endpoints;

/// <summary>
/// Writes a <c>multipart/form-data</c> request body as one object with a property per form part (CON-81). The generator
/// writes each <c>[FromForm]</c> parameter as its own object inside an <c>allOf</c>, which Kiota turns into a model
/// whose multipart writer throws "Expected a MultiPartBody instance", so a generated client couldn't upload. As one
/// object, the files part a binary array, the body is Kiota's <c>MultipartBody</c>: a part per field, a file with
/// its name. The parts the server reads are the same.
/// <para>
/// Only that shape is flattened: each member a plain object of properties (and which are required), no two naming
/// the same part. Anything else would lose a constraint or a part when merged, so spec generation fails instead of
/// publishing a form the server doesn't bind.
/// </para>
/// </summary>
internal sealed class MultipartFormTransformer : IOpenApiOperationTransformer
{
    private const string Multipart = "multipart/form-data";

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (operation.RequestBody is not OpenApiRequestBody { Content: { } content }
            || !content.TryGetValue(Multipart, out var media)
            || media.Schema is not OpenApiSchema { AllOf: { Count: > 0 } parts } form)
            return Task.CompletedTask;

        string where = $"{context.Description.HttpMethod} /{context.Description.RelativePath}";
        form.Properties ??= new Dictionary<string, IOpenApiSchema>();
        foreach (var member in parts)
        {
            if (member is not OpenApiSchema { Properties.Count: > 0 } part || !IsPlainObject(part))
                throw new InvalidOperationException(
                    $"{where}: a multipart form member that isn't a plain object of properties can't be flattened without losing what it says.");
            foreach (var (name, schema) in part.Properties!)
                if (!form.Properties.TryAdd(name, schema))
                    throw new InvalidOperationException($"{where}: two form members both name the part '{name}'.");
            foreach (var name in part.Required ?? new HashSet<string>())
                (form.Required ??= new HashSet<string>()).Add(name);
        }
        form.AllOf = null;
        form.Type = JsonSchemaType.Object;
        return Task.CompletedTask;
    }

    /// <summary>An object schema saying nothing but its properties and which are required, as the generator writes a form parameter.</summary>
    private static bool IsPlainObject(OpenApiSchema part) =>
        part.Type is null or JsonSchemaType.Object
        && part.AllOf is not { Count: > 0 } && part.AnyOf is not { Count: > 0 } && part.OneOf is not { Count: > 0 }
        && part.AdditionalProperties is null && part.Discriminator is null && part.Not is null
        && part.Enum is not { Count: > 0 } && part.MinProperties is null && part.MaxProperties is null;
}
