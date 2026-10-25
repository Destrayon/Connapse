using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Problems;

/// <summary>
/// A handler's 4xx, built as <see cref="ProblemResultFilter"/> builds one from <c>new { error, message }</c>:
/// the same body on the wire, from a named type instead of an anonymous object. <c>error</c> is the
/// machine-readable code or the text itself, and <c>detail</c> is the message, or the error when there is none.
/// </summary>
public static class ApiProblem
{
    public static IResult BadRequest(string error, string? message = null) =>
        Result(StatusCodes.Status400BadRequest, error, message);

    public static IResult Unauthorized(string error, string? message = null) =>
        Result(StatusCodes.Status401Unauthorized, error, message);

    /// <summary>A 400 listing several reasons, as <c>new { errors }</c> made one: no detail, an <c>errors</c> array.</summary>
    public static IResult BadRequest(IEnumerable<string> errors)
    {
        var problem = new ProblemDetails { Status = StatusCodes.Status400BadRequest };
        problem.Extensions["errors"] = errors.ToList();
        return Results.Problem(problem);
    }

    public static IResult Forbidden(string error, string? message = null) =>
        Result(StatusCodes.Status403Forbidden, error, message);

    public static IResult NotFound(string error, string? message = null) =>
        Result(StatusCodes.Status404NotFound, error, message);

    public static IResult Conflict(string error, string? message = null) =>
        Result(StatusCodes.Status409Conflict, error, message);

    private static IResult Result(int status, string error, string? message)
    {
        var problem = new ProblemDetails { Status = status, Detail = message ?? error };
        problem.Extensions["error"] = error;
        if (message is not null)
            problem.Extensions["message"] = message;
        return Results.Problem(problem);
    }
}
