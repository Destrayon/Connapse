using Connapse.Web.Problems;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Connapse.Core.Tests.OpenApi;

/// <summary>
/// CON-166: Identity's login, refresh and confirmEmail answer 401 through a result union the generator doesn't read,
/// so the transformer declares it on those three and nowhere else.
/// </summary>
[Trait("Category", "Unit")]
public class IdentitySignInUnauthorizedTransformerTests
{
    private static OpenApiOperationTransformerContext Context(string? path) => new()
    {
        DocumentName = "v1",
        Description = new ApiDescription { HttpMethod = "POST", RelativePath = path },
        ApplicationServices = new ServiceCollection().BuildServiceProvider(),
        Document = new OpenApiDocument
        {
            Components = new OpenApiComponents { Schemas = new Dictionary<string, IOpenApiSchema> { ["ProblemDetails"] = new OpenApiSchema() } },
        },
    };

    [Theory]
    [InlineData("api/v1/identity/login", "email or password is wrong")]
    [InlineData("api/v1/identity/refresh", "refresh token is invalid or expired")]
    [InlineData("api/v1/identity/confirmEmail", "confirmation code is wrong")]
    [InlineData("API/V1/IDENTITY/CONFIRMEMAIL", "confirmation code is wrong")]
    public async Task SignInRoute_Declares401AsAProblem(string path, string reason)
    {
        var operation = new OpenApiOperation();

        await new IdentitySignInUnauthorizedTransformer().TransformAsync(operation, Context(path), default);

        var unauthorized = (OpenApiResponse)operation.Responses!["401"];
        unauthorized.Description.Should().StartWith("Unauthorized: ").And.Contain(reason);
        var schema = unauthorized.Content!.Should().ContainSingle().Which;
        schema.Key.Should().Be("application/problem+json");
        schema.Value.Schema.Should().BeOfType<OpenApiSchemaReference>().Which.Reference.Id.Should().Be("ProblemDetails");
    }

    [Theory]
    [InlineData("api/v1/identity/register")]
    [InlineData("api/v1/identity/manage/info")]
    [InlineData(null)]
    public async Task OtherRoute_IsLeftAlone(string? path)
    {
        var operation = new OpenApiOperation();

        await new IdentitySignInUnauthorizedTransformer().TransformAsync(operation, Context(path), default);

        operation.Responses.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Declared401_IsKept()
    {
        var declared = new OpenApiResponse { Description = "declared" };
        var operation = new OpenApiOperation { Responses = new OpenApiResponses { ["401"] = declared } };

        await new IdentitySignInUnauthorizedTransformer().TransformAsync(operation, Context("api/v1/identity/login"), default);

        operation.Responses["401"].Should().BeSameAs(declared);
    }
}
