using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace Connapse.Identity.Authentication;

/// <summary>How a request's token answers "does a high-impact MCP call wait for a person?" (#782).</summary>
public static class McpApprovalClaims
{
    public const string RequiresApproval = "requires_approval";

    /// <summary>
    /// True unless the token was created with approval off. A session or OAuth token has no setting, so
    /// it requires approval: failing closed is the only safe reading of a missing answer.
    /// </summary>
    public static bool Required(ClaimsPrincipal user) =>
        !string.Equals(user.FindFirst(RequiresApproval)?.Value, "false", StringComparison.Ordinal);

    /// <summary>
    /// Which token is asking, so an approval binds to it: its PAT or agent key, or for a session or OAuth
    /// token, the signed-in user.
    /// </summary>
    public static string TokenKey(ClaimsPrincipal user) =>
        user.FindFirst("pat_id")?.Value is { } pat ? $"pat:{pat}"
        : user.FindFirst("agent_key_id")?.Value is { } key ? $"agent_key:{key}"
        : $"user:{user.FindFirst(ClaimTypes.NameIdentifier)?.Value}";

    /// <summary>Whether the caller is a PAT or an agent key rather than a person who signed in.</summary>
    public static bool IsToken(ClaimsPrincipal user) =>
        user.FindFirst("auth_method")?.Value is "api_key" or "agent_api_key";

    /// <summary>
    /// Whether the caller signed in to the UI in a browser. A bearer token, even one issued to a person
    /// through OAuth, is what an MCP client holds, so it must not decide the client's own held calls.
    /// </summary>
    public static bool IsBrowserSession(ClaimsPrincipal user) =>
        user.Identities.Any(i => i.IsAuthenticated && i.AuthenticationType == IdentityConstants.ApplicationScheme);

    /// <summary>The person a token acts for: an agent's creator, or the user themself.</summary>
    public static Guid? OwnerUserId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirst("on_behalf_of")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
}
