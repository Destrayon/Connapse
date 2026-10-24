using System.Reflection;
using Connapse.Application;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Problems;

/// <summary>
/// Turns an operation's failure status into the ProblemDetails REST answers with, from the
/// <see cref="FailureAttribute"/> on the status value.
/// </summary>
public static class OperationProblems
{
    /// <summary>
    /// Where every Connapse problem type is documented; a slug is the heading of its entry in
    /// docs/problems.md, so the type resolves to that entry.
    /// </summary>
    public const string TypeBase = "https://github.com/Destrayon/Connapse/blob/main/docs/problems.md#";

    public static ProblemDetails For<TStatus>(TStatus status, string? detail = null)
        where TStatus : struct, Enum
    {
        var failure = Failure(status)
            ?? throw new InvalidOperationException(
                $"{typeof(TStatus).Name}.{status} has no [Failure], so it has no HTTP status or problem type.");

        var problem = new ProblemDetails
        {
            Type = TypeBase + failure.Slug,
            Title = failure.Title,
            Status = failure.HttpStatus,
            Detail = detail,
        };
        problem.Extensions["error"] = detail ?? failure.Title;
        return problem;
    }

    public static IResult Result<TStatus>(TStatus status, string? detail = null)
        where TStatus : struct, Enum => Results.Problem(For(status, detail));

    /// <summary>The <see cref="FailureAttribute"/> on one enum value, or null when it has none.</summary>
    public static FailureAttribute? Failure(Enum status) =>
        status.GetType().GetField(status.ToString(), BindingFlags.Public | BindingFlags.Static)
            ?.GetCustomAttribute<FailureAttribute>();
}
