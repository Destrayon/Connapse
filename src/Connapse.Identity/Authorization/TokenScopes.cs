using System.Security.Claims;
using Connapse.Identity.Authentication;

namespace Connapse.Identity.Authorization;

/// <summary>
/// Whether a caller may act with a scope. A token (a personal access token or an agent key) is limited to the
/// scopes it was issued with, so one issued with none has none: it carries no scope claim, and only its sign-in
/// method tells it from a person who signed in (#932). A sign-in carries no scope claim and is limited by its
/// role alone.
/// </summary>
public static class TokenScopes
{
    public static bool Allows(ClaimsPrincipal user, string scope) =>
        user.HasClaim("scope", scope) || (!user.HasClaim(c => c.Type == "scope") && !McpApprovalClaims.IsToken(user));
}
