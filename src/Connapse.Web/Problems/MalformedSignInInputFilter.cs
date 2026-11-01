using System.Reflection;

namespace Connapse.Web.Problems;

/// <summary>
/// Refuses sign-in requests whose text can't name an account, before Identity looks one up (CON-165). PostgreSQL
/// refuses a NUL character in text, and Identity parses a user id as a Guid, so either used to reach the database
/// or the parser and answer 500. A NUL anywhere in the request's text is a 400; a confirmEmail user id that isn't
/// a Guid is the 401 Identity gives an unknown user.
/// </summary>
public sealed class MalformedSignInInputFilter : IEndpointFilter
{
    public const string ConfirmEmailRoute = "/api/v1/identity/confirmEmail";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (request.Path.Equals(ConfirmEmailRoute, StringComparison.OrdinalIgnoreCase)
            && !Guid.TryParse(request.Query["userId"], out _))
            return TypedResults.Unauthorized();

        if (context.Arguments.Any(HasNul))
            return ApiProblem.BadRequest("invalid-text", "Request text can't contain a NUL character.");

        return await next(context);
    }

    // Only the request's own values: a string, or the string members of a request record. Injected services are
    // arguments too, and their properties aren't the caller's input.
    private static bool HasNul(object? argument) => argument switch
    {
        string text => text.Contains('\0', StringComparison.Ordinal),
        null => false,
        _ when IsRequestRecord(argument.GetType()) => argument.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0)
            .Any(p => p.GetValue(argument) is string text && text.Contains('\0', StringComparison.Ordinal)),
        _ => false,
    };

    private static bool IsRequestRecord(Type type) =>
        type.Namespace is "Microsoft.AspNetCore.Identity.Data" or "Connapse.Core";
}
