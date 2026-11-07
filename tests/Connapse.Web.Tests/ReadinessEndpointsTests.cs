using Connapse.Core;
using Connapse.Web.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Connapse.Web.Tests;

[Trait("Category", "Unit")]
public class ReadinessEndpointsTests
{
    [Theory]
    [InlineData(LocalModelState.Ready)]
    [InlineData(LocalModelState.NotManaged)]
    public void Report_SearchCanRun_AnswersOk(LocalModelState state)
    {
        IResult result = ReadinessEndpoints.Report(new LocalModelStatus(state));

        result.Should().BeOfType<Ok<ReadinessReport>>().Which.Value!.State.Should().Be(state.ToString());
    }

    [Fact]
    public void Report_ModelDownloading_AnswersUnavailableWithProgress()
    {
        IResult result = ReadinessEndpoints.Report(new LocalModelStatus(LocalModelState.Downloading, "nomic-embed-text", 42));

        var problem = result.Should().BeOfType<ProblemHttpResult>().Which;
        problem.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        problem.ProblemDetails.Detail.Should().Contain("42%");
        problem.ProblemDetails.Extensions["percent"].Should().Be(42);
    }
}
