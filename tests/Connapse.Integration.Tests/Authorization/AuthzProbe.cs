using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;

namespace Connapse.Integration.Tests.Authorization;

/// <summary>
/// Lets the authorization matrix ask "would this principal get in?" without running the endpoint.
/// A request carrying <see cref="RequestHeader"/> that passes authorization is answered here with
/// <see cref="AllowedHeader"/>, so probing every route as an admin deletes, resets and enqueues
/// nothing. Every other request, and every refusal, goes to the framework's own handler unchanged.
/// </summary>
public sealed class AuthzProbe : IAuthorizationMiddlewareResultHandler
{
    public const string RequestHeader = "X-Authz-Probe";
    public const string AllowedHeader = "X-Authz-Probe-Allowed";

    private readonly AuthorizationMiddlewareResultHandler _framework = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Succeeded && context.Request.Headers.ContainsKey(RequestHeader))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            context.Response.Headers[AllowedHeader] = "true";
            return Task.CompletedTask;
        }

        return _framework.HandleAsync(next, context, policy, authorizeResult);
    }
}
