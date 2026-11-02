using Connapse.Identity.Authorization;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Connapse.Web.Mcp;

/// <summary>
/// With Mcp:AllowAnonymousDiscovery on, an unauthenticated client (a registry, the conformance runner)
/// may read every tool's schema, as before tools carried scopes (#782). The SDK checks a tool's scope
/// policy for both listing and calling, so this passes it for an anonymous tools/list or tools/call;
/// the anonymous call-tool filter then refuses the call itself with "Authentication required". It is
/// registered only in that mode, and never applies to a signed-in user or a token.
/// </summary>
public sealed class AnonymousMcpDiscoveryHandler : AuthorizationHandler<ScopeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ScopeRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true
            && context.Resource is RequestContext<ListToolsRequestParams> or RequestContext<CallToolRequestParams>)
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}
