using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Connapse.Identity.Authorization;

public class ScopeAuthorizationHandler : AuthorizationHandler<ScopeRequirement>
{
    // Role → implicit scopes mapping
    private static readonly Dictionary<string, HashSet<string>> RoleScopeMap = new()
    {
        ["Admin"] = ["knowledge:read", "knowledge:write", "admin:users", "agent:ingest"],
        ["Editor"] = ["knowledge:read", "knowledge:write"],
        ["Viewer"] = ["knowledge:read"],
        ["Agent"] = ["knowledge:read", "agent:ingest"],
    };

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ScopeRequirement requirement)
    {
        // Check explicit scope claims (from PAT tokens)
        var scopeClaims = context.User.FindAll("scope").Select(c => c.Value).ToHashSet();
        if (scopeClaims.Contains(requirement.Scope))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // Check role-derived scopes (for cookie/JWT users)
        var roles = context.User.FindAll(ClaimTypes.Role).Select(c => c.Value);
        foreach (var role in roles)
        {
            if (RoleScopeMap.TryGetValue(role, out var implicitScopes) &&
                implicitScopes.Contains(requirement.Scope))
            {
                context.Succeed(requirement);
                return Task.CompletedTask;
            }
        }

        return Task.CompletedTask;
    }
}
