using Connapse.Web.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Connapse.Core.Tests.OpenApi;

/// <summary>
/// CON-81: the multipart form transformer flattens only the shape the generator writes for [FromForm] parameters,
/// and refuses anything it would change by merging, so the spec never describes a form the server doesn't bind.
/// </summary>
[Trait("Category", "Unit")]
public class MultipartFormTransformerTests
{
    private static OpenApiOperationTransformerContext Context() => new()
    {
        DocumentName = "v1",
        Description = new ApiDescription { HttpMethod = "POST", RelativePath = "api/upload" },
        ApplicationServices = new ServiceCollection().BuildServiceProvider(),
    };

    private static OpenApiSchema Part(string name, IOpenApiSchema schema, bool required = false) => new()
    {
        Type = JsonSchemaType.Object,
        Properties = new Dictionary<string, IOpenApiSchema> { [name] = schema },
        Required = required ? new HashSet<string> { name } : null,
    };

    private static OpenApiOperation Form(params IOpenApiSchema[] parts) => new()
    {
        RequestBody = new OpenApiRequestBody
        {
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["multipart/form-data"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.Object, AllOf = [.. parts] } },
            },
        },
    };

    private static OpenApiSchema Body(OpenApiOperation operation) =>
        (OpenApiSchema)((OpenApiRequestBody)operation.RequestBody!).Content!["multipart/form-data"].Schema!;

    private static readonly OpenApiSchema Binary = new() { Type = JsonSchemaType.String, Format = "binary" };
    private static readonly OpenApiSchema Text = new() { Type = JsonSchemaType.String };

    [Fact]
    public async Task PlainDisjointParts_BecomeOneObjectWithTheirRequiredParts()
    {
        var operation = Form(Part("files", new OpenApiSchema { Type = JsonSchemaType.Array, Items = Binary }, required: true), Part("path", Text));

        await new MultipartFormTransformer().TransformAsync(operation, Context(), default);

        var body = Body(operation);
        body.AllOf.Should().BeNull();
        body.Properties!.Keys.Should().BeEquivalentTo(["files", "path"]);
        body.Required.Should().BeEquivalentTo(["files"]);
    }

    [Fact]
    public async Task TwoPartsWithOneName_FailGeneration()
    {
        var operation = Form(Part("path", Text), Part("path", Binary));

        var act = () => new MultipartFormTransformer().TransformAsync(operation, Context(), default);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*POST /api/upload*'path'*");
    }

    [Fact]
    public async Task AConstrainedPart_FailsGenerationRatherThanLosingItsConstraint()
    {
        var constrained = Part("path", Text);
        constrained.AdditionalProperties = new OpenApiSchema { Type = JsonSchemaType.String };
        var operation = Form(Part("files", Binary), constrained);

        var act = () => new MultipartFormTransformer().TransformAsync(operation, Context(), default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AFormThatIsAlreadyOneObject_IsLeftAlone()
    {
        var flat = new OpenApiSchema { Type = JsonSchemaType.Object, Properties = new Dictionary<string, IOpenApiSchema> { ["files"] = Binary } };
        var operation = new OpenApiOperation
        {
            RequestBody = new OpenApiRequestBody { Content = new Dictionary<string, OpenApiMediaType> { ["multipart/form-data"] = new() { Schema = flat } } },
        };

        await new MultipartFormTransformer().TransformAsync(operation, Context(), default);

        Body(operation).Should().BeSameAs(flat);
        flat.Properties.Keys.Should().Equal("files");
    }
}
