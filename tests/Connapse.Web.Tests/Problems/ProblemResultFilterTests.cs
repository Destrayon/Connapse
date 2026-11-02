using System.Text.Json;
using Connapse.Application;
using Connapse.Web.Problems;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Connapse.Web.Tests.Problems;

[Trait("Category", "Unit")]
public class ProblemResultFilterTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static ProblemHttpResult Problem(IResult result) =>
        ProblemResultFilter.ToProblem(result, Web).Should().BeOfType<ProblemHttpResult>().Subject;

    [Fact]
    public void ToProblem_ErrorBody_KeepsFieldsAsMembers()
    {
        var problem = Problem(Results.BadRequest(new { error = "topk_out_of_range", message = "topK must be between 1 and 100", field = "topK" })).ProblemDetails;

        problem.Status.Should().Be(400);
        problem.Detail.Should().Be("topK must be between 1 and 100");
        problem.Extensions["error"].Should().BeOfType<JsonElement>().Which.GetString().Should().Be("topk_out_of_range");
        problem.Extensions["field"].Should().BeOfType<JsonElement>().Which.GetString().Should().Be("topK");
    }

    [Fact]
    public void ToProblem_JsonResultWithStatus_BecomesProblemWithThatStatus()
    {
        var problem = Problem(Results.Json(new { error = "upstream failed" }, statusCode: 502)).ProblemDetails;

        problem.Status.Should().Be(502);
        problem.Detail.Should().Be("upstream failed");
    }

    [Fact]
    public void ToProblem_FieldNamedLikeAProblemMember_IsNotWrittenTwice()
    {
        var problem = Problem(Results.Conflict(new { error = "taken", status = "Failed" })).ProblemDetails;

        problem.Status.Should().Be(409);
        problem.Extensions.Should().NotContainKey("status");
    }

    [Fact]
    public void ToProblem_EmptyFailure_BecomesProblem()
    {
        Problem(Results.NotFound()).ProblemDetails.Status.Should().Be(404);
        Problem(Results.Unauthorized()).ProblemDetails.Status.Should().Be(401);
    }

    [Fact]
    public void ToProblem_SuccessOrProblem_IsLeftAlone()
    {
        ProblemResultFilter.ToProblem(Results.Ok(new { error = "not one" }), Web).Should().BeNull();
        ProblemResultFilter.ToProblem(Results.Problem(statusCode: 500), Web).Should().BeNull();
        ProblemResultFilter.ToProblem(Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["required"] }), Web).Should().BeNull();
    }

    public enum SampleStatus
    {
        Success,
        [Failure(404, "sample-not-found", "Sample not found")]
        NotFound,
        Unmapped,
    }

    [Fact]
    public void For_MappedFailure_HasStableTypeTitleAndError()
    {
        var problem = OperationProblems.For(SampleStatus.NotFound, "Sample 7 is gone");

        problem.Type.Should().Be(OperationProblems.TypeBase + "sample-not-found");
        problem.Title.Should().Be("Sample not found");
        problem.Status.Should().Be(404);
        problem.Detail.Should().Be("Sample 7 is gone");
        problem.Extensions["error"].Should().Be("Sample 7 is gone");
    }

    [Fact]
    public void For_FailureWithoutAttribute_Throws()
    {
        var map = () => OperationProblems.For(SampleStatus.Unmapped);

        map.Should().Throw<InvalidOperationException>();
    }
}
