using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Connapse.Identity.Authorization;

public class ScopeAuthorizationHandler : AuthorizationHandler<ScopeRequirement>
{
    // Role → implicit scopes mapping
    private static readonly Dictionary<string, HashSet<string>> RoleScopeMap = new()
    {
        ["Owner"] = ["knowledge:read", "knowledge:write", "admin:users", "admin:manage-admins"],
        ["Admin"] = ["knowledge:read", "knowledge:write", "admin:users"],
        ["Editor"] = ["knowledge:read", "knowledge:write"],
        ["Viewer"] = ["knowledge:read"],
        ["Agent"] = ["knowledge:read"],
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

        // A token issued with scopes is limited to them. Its principal also carries its owner's roles,
        // and letting those imply scopes would give an Owner's knowledge:read token every scope (#782).
        if (scopeClaims.Count > 0)
            return Task.CompletedTask;

        // A token issued with no scopes has none: its owner's roles don't stand in for them (#932).
        if (TokenScopes.IsToken(context.User))
            return Task.CompletedTask;

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
