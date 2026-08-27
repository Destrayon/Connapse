using System.Security.Claims;

namespace Connapse.Web.Services;

/// <summary>
/// Works out which person a search is running for.
/// </summary>
/// <remarks>
/// One place, because four surfaces ask the question — the REST endpoints, the Blazor page, the MCP
/// tools, and agent-authenticated requests — and a surface that answers it differently is a hole in
/// the filter rather than an inconsistency. There are already four private copies of a
/// <c>GetUserId</c> in the endpoint classes; this deliberately does not become a fifth.
/// <para>
/// It answers <b>who</b>, never <b>whether</b>. Authorisation stays where it is; this only reports
/// the identity that a permission filter will later resolve scopes for (#421).
/// </para>
/// </remarks>
public static class SearchPrincipal
{
    /// <summary>The claim naming the user an agent acts for.</summary>
    public const string OnBehalfOfClaim = "on_behalf_of";

    /// <summary>The claim distinguishing an agent's key from a person's.</summary>
    public const string ActorTypeClaim = "actor_type";

    /// <summary>
    /// The user this request is for, or null when there is not one.
    /// </summary>
    /// <remarks>
    /// An agent authenticates as itself: <see cref="ClaimTypes.NameIdentifier"/> holds the agent's
    /// id, not a person's. Reading it as a user id would hand the filter a Guid that matches no
    /// user and resolve to no scopes — denial by accident rather than by decision, and impossible
    /// to tell from a real denial. So an agent is resolved through the user it was created by.
    /// <para>
    /// Null is a real answer, not a failure: an unauthenticated request has no user, and so will a
    /// standalone agent once agents can exist without one.
    /// </para>
    /// </remarks>
    public static Guid? Resolve(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
            return null;

        string? claim = principal.FindFirstValue(ActorTypeClaim) == "agent"
            ? principal.FindFirstValue(OnBehalfOfClaim)
            : principal.FindFirstValue(ClaimTypes.NameIdentifier);

        return Guid.TryParse(claim, out var id) ? id : null;
    }
}
