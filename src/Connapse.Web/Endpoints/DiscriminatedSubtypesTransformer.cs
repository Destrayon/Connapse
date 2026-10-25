using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Connapse.Web.Endpoints;

/// <summary>
/// Writes each polymorphic type (a <c>[JsonPolymorphic]</c> base such as <c>SourceScope</c>) so a client generator
/// gets one class per derived type, while a validator still checks a value against the one it names (CON-77).
/// The generator writes them as an <c>anyOf</c>, which Kiota merges into one class that knows only the first type's
/// discriminator value, so a generated client couldn't describe a GitHub or Jira scope. Here:
/// <list type="bullet">
/// <item>the base declares the discriminator, its values as an enum, and that it is required;</item>
/// <item>each subtype is <c>allOf</c> the base and its own members, keeping its own single discriminator value;</item>
/// <item>everywhere the base is used, a <c>oneOf</c> of the subtypes with the discriminator stands in its place,
/// so a value must be exactly one subtype, with that subtype's required members.</item>
/// </list>
/// Base and subtypes describe exactly the JSON the API accepts and returns.
/// </summary>
internal sealed class DiscriminatedSubtypesTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        var schemas = document.Components?.Schemas;
        if (schemas is null)
            return Task.CompletedTask;

        var unions = new Dictionary<string, Union>(StringComparer.Ordinal);
        foreach (var (name, entry) in schemas.ToList())
        {
            if (entry is not OpenApiSchema { Discriminator: { PropertyName: { } property, Mapping: { Count: > 0 } mapping }, AnyOf.Count: > 0 } polymorphic)
                continue;

            var subtypes = new List<string>();
            foreach (var (_, target) in mapping)
            {
                string subtype = target.Reference.Id!;
                if (schemas[subtype] is not OpenApiSchema own)
                    continue;
                subtypes.Add(subtype);
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

            unions[name] = new Union(subtypes, polymorphic.Discriminator, document);
        }
        if (unions.Count == 0)
            return Task.CompletedTask;

        // Every use of a base becomes the union, except the subtypes' own allOf, which is what makes them subtypes.
        foreach (var (name, entry) in schemas.ToList())
        {
            if (entry is OpenApiSchema { AllOf: [OpenApiSchemaReference { Reference.Id: { } parent }, ..] } && unions.ContainsKey(parent))
                continue;
            schemas[name] = Replace(entry, unions);
        }
        foreach (var operation in (document.Paths?.Values.AsEnumerable() ?? []).SelectMany(path => path.Operations?.Values.AsEnumerable() ?? []))
        {
            foreach (var parameter in (operation.Parameters ?? []).OfType<OpenApiParameter>())
                if (parameter.Schema is { } schema)
                    parameter.Schema = Replace(schema, unions);
            foreach (var media in Media(operation.RequestBody as OpenApiRequestBody))
                if (media.Schema is { } schema)
                    media.Schema = Replace(schema, unions);
            foreach (var media in (operation.Responses ?? []).Values.OfType<OpenApiResponse>().SelectMany(Media))
                if (media.Schema is { } schema)
                    media.Schema = Replace(schema, unions);
        }
        return Task.CompletedTask;
    }

    /// <summary>A base's stand-in: its subtypes and its discriminator.</summary>
    private sealed record Union(List<string> Subtypes, OpenApiDiscriminator Discriminator, OpenApiDocument Document)
    {
        public IEnumerable<IOpenApiSchema> Members => Subtypes.Select(s => (IOpenApiSchema)new OpenApiSchemaReference(s, Document));

        public OpenApiSchema Schema() => new() { OneOf = [.. Members], Discriminator = Discriminator };
    }

    private static IEnumerable<OpenApiMediaType> Media(OpenApiRequestBody? body) => body?.Content?.Values ?? [];

    private static IEnumerable<OpenApiMediaType> Media(OpenApiResponse response) => response.Content?.Values ?? [];

    /// <summary>The schema with every reference to a base replaced by its union, at any depth.</summary>
    private static IOpenApiSchema Replace(IOpenApiSchema schema, Dictionary<string, Union> unions)
    {
        if (schema is OpenApiSchemaReference { Reference.Id: { } id } && unions.TryGetValue(id, out var union))
            return union.Schema();
        if (schema is not OpenApiSchema inline)
            return schema;

        if (inline.Properties is { } properties)
            foreach (var key in properties.Keys.ToList())
                properties[key] = Replace(properties[key], unions);
        if (inline.Items is { } items)
            inline.Items = Replace(items, unions);
        if (inline.AdditionalProperties is { } additional)
            inline.AdditionalProperties = Replace(additional, unions);
        foreach (var list in new[] { inline.AllOf, inline.AnyOf })
            if (list is not null)
                for (int i = 0; i < list.Count; i++)
                    list[i] = Replace(list[i], unions);

        // Inside a oneOf, such as a nullable [null, base], the subtypes join the list itself: a oneOf nested in
        // a oneOf is something client generators give up on and type as untyped JSON.
        if (inline.OneOf is { } oneOf)
        {
            var members = new List<IOpenApiSchema>();
            foreach (var member in oneOf)
            {
                if (member is OpenApiSchemaReference { Reference.Id: { } memberId } && unions.TryGetValue(memberId, out var memberUnion))
                {
                    members.AddRange(memberUnion.Members);
                    inline.Discriminator ??= memberUnion.Discriminator;
                }
                else
                    members.Add(Replace(member, unions));
            }
            inline.OneOf = members;
        }
        return inline;
    }
}
