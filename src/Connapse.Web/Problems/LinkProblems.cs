using Connapse.Application;
using Connapse.Application.Links;

namespace Connapse.Web.Problems;

/// <summary>
/// A failed cloud identity unlink's problem. Before unlinking moved onto its operation (#783), an unknown provider
/// and an AWS link that changed mid-disconnect were answered with a code in <c>error</c>, which scripts branch on,
/// so they keep it beside the problem type, as search does. docs/self-host/upgrade.md lists each code with its type.
/// </summary>
public static class LinkProblems
{
    public static IResult Result<TResult>(Attempt<TResult, AccountUnlinkStatus> failed)
    {
        var problem = OperationProblems.For(failed.Status, failed.Detail);
        if (failed.Field is { } field)
            problem.Extensions["field"] = field;
        if (ErrorCode(failed.Status) is { } code)
            problem.Extensions["error"] = code;
        return Results.Problem(problem);
    }

    public static string? ErrorCode(AccountUnlinkStatus status) => status switch
    {
        AccountUnlinkStatus.InvalidProvider => "invalid_provider",
        AccountUnlinkStatus.Changed => "link_changed",
        _ => null,
    };
}
