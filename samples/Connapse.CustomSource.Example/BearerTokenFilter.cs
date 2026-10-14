using System.Security.Cryptography;
using System.Text;

namespace Connapse.CustomSource.Example;

/// <summary>Refuses any request whose bearer token is not the configured one.</summary>
public sealed class BearerTokenFilter(ExampleConfig config) : IEndpointFilter
{
    private const string Prefix = "Bearer ";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        string header = context.HttpContext.Request.Headers.Authorization.ToString();
        bool valid = header.StartsWith(Prefix, StringComparison.Ordinal)
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(header[Prefix.Length..]),
                Encoding.UTF8.GetBytes(config.Token));

        return valid
            ? await next(context)
            : Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Missing or wrong bearer token.");
    }
}
