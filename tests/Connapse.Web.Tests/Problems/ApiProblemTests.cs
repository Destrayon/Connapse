using System.Text.Json;
using Connapse.Web.Problems;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Connapse.Web.Tests.Problems;

[Trait("Category", "Unit")]
public class ApiProblemTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string Body(IResult result) =>
        JsonSerializer.Serialize(result.Should().BeOfType<ProblemHttpResult>().Subject.ProblemDetails, Web);

    private static string Filtered(IResult anonymous) => Body(ProblemResultFilter.ToProblem(anonymous, Web)!);

    [Fact]
    public void ApiProblem_ErrorOnly_IsTheBodyTheFilterMadeOfTheAnonymousObject()
    {
        Body(ApiProblem.NotFound("Container x not found"))
            .Should().Be(Filtered(Results.NotFound(new { error = "Container x not found" })));
        Body(ApiProblem.BadRequest("skip must be >= 0"))
            .Should().Be(Filtered(Results.BadRequest(new { error = "skip must be >= 0" })));
        Body(ApiProblem.Conflict("Folder '/a/' already exists"))
            .Should().Be(Filtered(Results.Conflict(new { error = "Folder '/a/' already exists" })));
    }

    [Fact]
    public void ApiProblem_ErrorAndMessage_IsTheBodyTheFilterMadeOfTheAnonymousObject()
    {
        Body(ApiProblem.BadRequest("document_not_ready", "Document is still being ingested"))
            .Should().Be(Filtered(Results.BadRequest(new { error = "document_not_ready", message = "Document is still being ingested" })));
        Body(ApiProblem.Forbidden("cloud_access_denied", "Access denied"))
            .Should().Be(Filtered(Results.Json(new { error = "cloud_access_denied", message = "Access denied" }, statusCode: 403)));
        Body(ApiProblem.Unauthorized("Invalid or expired refresh token"))
            .Should().Be(Filtered(Results.Json(new { error = "Invalid or expired refresh token" }, statusCode: 401)));
    }

    [Fact]
    public void ApiProblem_ErrorAndField_IsTheBodyTheFilterMadeOfTheAnonymousObject()
    {
        Body(ApiProblem.BadRequest("product must be \"confluence\" or \"jira\".", field: "product"))
            .Should().Be(Filtered(Results.BadRequest(new { error = "product must be \"confluence\" or \"jira\".", field = "product" })));
        Body(ApiProblem.Conflict("A connection named 'x' already exists.", field: "name"))
            .Should().Be(Filtered(Results.Conflict(new { error = "A connection named 'x' already exists.", field = "name" })));
        Body(ApiProblem.BadGateway("Atlassian didn't answer."))
            .Should().Be(Filtered(Results.Json(new { error = "Atlassian didn't answer." }, statusCode: 502)));
    }

    [Fact]
    public void ApiProblem_Errors_IsTheBodyTheFilterMadeOfTheAnonymousObject()
    {
        string[] reasons = ["Role 'Ghost' does not exist.", "Second reason."];

        Body(ApiProblem.BadRequest(reasons))
            .Should().Be(Filtered(Results.BadRequest(new { errors = reasons.Select(r => r) })));
    }
}
