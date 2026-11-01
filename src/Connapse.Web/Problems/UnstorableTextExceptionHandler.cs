using Microsoft.AspNetCore.Diagnostics;
using Npgsql;

namespace Connapse.Web.Problems;

/// <summary>
/// Answers 400, not 500, when PostgreSQL refuses to store text from a request (CON-179). It refuses a NUL
/// character in a text column (22021) and a <c>\u0000</c> escape in jsonb (22P05), and request text can reach
/// either through any route's body, query or path. Mapping the database's own refusal covers every route at once,
/// and it only changes responses that were already failures: a call that works today never meets it.
/// </summary>
public sealed class UnstorableTextExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (!httpContext.IsApiRequest() || !IsUnstorableText(exception))
            return false;

        await ApiProblem.BadRequest("invalid-text", "Request text can't contain a NUL character.").ExecuteAsync(httpContext);
        return true;
    }

    internal static bool IsUnstorableText(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is PostgresException { SqlState: PostgresErrorCodes.CharacterNotInRepertoire or PostgresErrorCodes.UntranslatableCharacter })
                return true;
        }

        return false;
    }
}
