using System.Reflection;
using System.Runtime.CompilerServices;

namespace Connapse.Web.Problems;

/// <summary>
/// Refuses sign-in requests whose text can't name an account, before Identity looks one up (CON-165). PostgreSQL
/// refuses a NUL character in text, and Identity parses a user id as a Guid, so either used to reach the database
/// or the parser and answer 500. A NUL anywhere in the request's text is a 400; a confirmEmail user id that isn't
/// a Guid is the 401 Identity gives an unknown user. A required field sent as null is the validation 400 a missing
/// one gets (CON-206): JSON binding only checks that the field is present, and Identity hands the null to a lookup
/// that throws.
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

        var nullFields = context.Arguments.SelectMany(NullRequiredFields).ToArray();
        if (nullFields.Length > 0)
        {
            // Login's and refresh's declared 400 require error. CustomizeProblemDetails adds it when a problem writer
            // takes the response; setting it here keeps it when none does, such as for an Accept header no writer serves.
            var errors = nullFields.ToDictionary(f => f, f => new[] { $"The {f} field is required." });
            return TypedResults.ValidationProblem(errors, extensions: new Dictionary<string, object?> { ["error"] = errors.First().Value[0] });
        }

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

    private static IEnumerable<string> NullRequiredFields(object? argument) =>
        argument is not null && IsRequestRecord(argument.GetType())
            ? argument.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(string) && p.IsDefined(typeof(RequiredMemberAttribute)) && p.GetValue(argument) is null)
                .Select(p => p.Name)
            : [];

    private static bool IsRequestRecord(Type type) =>
        type.Namespace is "Microsoft.AspNetCore.Identity.Data" or "Connapse.Core";
}
