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

    public const string Admin = "RequireAdmin";
}
