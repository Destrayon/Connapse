using System.Security.Claims;
using Connapse.Identity.Authentication;

namespace Connapse.Identity.Authorization;

/// <summary>
/// Whether a caller may act with a scope. A token (a personal access token, an agent key, or an OAuth client's
/// access token) is limited to the scopes it was issued with, so one issued with none has none: it carries no
/// scope claim, and only its sign-in method or its client tells it from a person who signed in (#932). A
/// sign-in carries no scope claim and is limited by its role alone.
/// </summary>
public static class TokenScopes
{
    public static bool Allows(ClaimsPrincipal user, string scope) =>
        user.HasClaim("scope", scope) || (!user.HasClaim(c => c.Type == "scope") && !IsToken(user));

    /// <summary>A personal access token or agent key, or an access token issued to an OAuth client.</summary>
    public static bool IsToken(ClaimsPrincipal user) =>
        McpApprovalClaims.IsToken(user) || user.HasClaim(c => c.Type == "client_id");
}
