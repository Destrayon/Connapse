using System.Security.Claims;

namespace Connapse.Application.Ports;

/// <summary>
/// Whether a caller passes one of the authorization policies the REST endpoints declare. Web
/// answers it with ASP.NET Core's authorization service, which Connapse.Application can't reference,
/// so an operation makes the same check whichever surface called it.
/// </summary>
public interface IOperationAuthorizer
{
    Task<bool> AllowsAsync(ClaimsPrincipal user, string policy);
}

/// <summary>The policy names operations check, as Identity defines them.</summary>
public static class Policies
{
    public const string Viewer = "RequireViewer";

    public const string Editor = "RequireEditor";

    /// <summary>A Viewer or above, or an agent key: who may read knowledge over MCP.</summary>
    public const string Reader = "RequireReader";

    /// <summary>An Editor or above, or an agent key granted knowledge:write: who may change knowledge over MCP.</summary>
    public const string Writer = "RequireWriter";

    public const string Admin = "RequireAdmin";

    /// <summary>
    /// The knowledge:read scope: a person's roles imply it from Viewer up, and an agent key or token carries
    /// it, so it is the one check a read can make on every surface, MCP included.
    /// </summary>
    public const string KnowledgeRead = "Scope:KnowledgeRead";

    /// <summary>A person's own account, through any sign-in or their own token; never an agent key.</summary>
    public const string Account = "RequireAccount";

    /// <summary>A person signing in: a browser session or their own bearer token, never a personal access token or agent key.</summary>
    public const string Person = "RequirePerson";

    /// <summary>An administrator; a token of theirs only with the admin:audit scope.</summary>
    public const string AuditReader = "RequireAuditReader";

    /// <summary>An administrator; a token of theirs only with the admin:oauth-clients scope.</summary>
    public const string ClientAdmin = "RequireClientAdmin";
}
