using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Connapse.Web.Endpoints;

/// <summary>
/// Writes each polymorphic type (a <c>[JsonPolymorphic]</c> base such as <c>SourceScope</c>) as a base with a
/// discriminator and one subtype per derived type, each <c>allOf</c> the base and its own members (CON-77).
/// The generator writes them as an <c>anyOf</c>, which a client generator such as Kiota merges into one class
/// that knows only the first type's discriminator value, so a generated client couldn't describe a GitHub or
/// Jira scope. Base and subtypes accept exactly the JSON they did.
/// </summary>
internal sealed class DiscriminatedSubtypesTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        var schemas = document.Components?.Schemas;
        if (schemas is null)
            return Task.CompletedTask;

        foreach (var (name, entry) in schemas.ToList())
        {
            if (entry is not OpenApiSchema { Discriminator: { PropertyName: { } property, Mapping: { Count: > 0 } mapping }, AnyOf.Count: > 0 } polymorphic)
                continue;

            foreach (var (value, target) in mapping)
            {
                string subtype = target.Reference.Id!;
                if (schemas[subtype] is not OpenApiSchema own)
                    continue;
                // The discriminator lives on the base now; a subtype keeps only its own members.
                own.Properties?.Remove(property);
                own.Required?.Remove(property);
                schemas[subtype] = new OpenApiSchema { AllOf = [new OpenApiSchemaReference(name, document), own] };
            }

            polymorphic.AnyOf = null;
            polymorphic.Type = JsonSchemaType.Object;
            polymorphic.Properties ??= new Dictionary<string, IOpenApiSchema>();
            polymorphic.Properties[property] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Enum = [.. mapping.Keys.Select(key => (JsonNode)JsonValue.Create(key))],
            };
            polymorphic.Required ??= new HashSet<string>();
            polymorphic.Required.Add(property);
        }
        return Task.CompletedTask;
    }
}
