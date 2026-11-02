using Connapse.Application;
using Connapse.Application.Links;
using Connapse.Web.Problems;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Tests.Problems;

[Trait("Category", "Unit")]
public class LinkProblemsTests
{
    private static ProblemDetails Problem(IResult result) =>
        result.Should().BeOfType<ProblemHttpResult>().Subject.ProblemDetails;

    [Theory]
    [InlineData(AccountUnlinkStatus.InvalidProvider, "invalid-link-provider", 400, "invalid_provider")]
    [InlineData(AccountUnlinkStatus.Changed, "link-changed", 409, "link_changed")]
    public void Result_UnlinkFailure_CarriesProblemTypeAndOldCode(AccountUnlinkStatus status, string slug, int httpStatus, string code)
    {
        var problem = Problem(LinkProblems.Result(Attempt<UnlinkAccountRequest, AccountUnlinkStatus>.Fail(status, "what went wrong")));

        problem.Type.Should().Be(OperationProblems.TypeBase + slug);
        problem.Status.Should().Be(httpStatus);
        problem.Detail.Should().Be("what went wrong");
        problem.Extensions["error"].Should().Be(code, "scripts written before #783 branch on the code");
    }

    [Theory]
    [InlineData(AccountUnlinkStatus.Forbidden)]
    [InlineData(AccountUnlinkStatus.NotFound)]
    [InlineData(AccountUnlinkStatus.Unavailable)]
    public void Result_UnlinkFailureThatNeverHadACode_KeepsDetailInError(AccountUnlinkStatus status)
    {
        var problem = Problem(LinkProblems.Result(Attempt<UnlinkAccountRequest, AccountUnlinkStatus>.Fail(status, "what went wrong")));

        problem.Extensions["error"].Should().Be("what went wrong");
    }
}
