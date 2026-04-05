namespace Connapse.Identity.Services;

/// <summary>
/// Determines whether a user is authorized to invite others with a given role.
/// The default implementation checks ASP.NET Identity roles; Cloud overrides
/// this to check org membership instead.
/// </summary>
public interface IInviteAuthorizationPolicy
{
    Task<bool> CanInviteWithRoleAsync(Guid inviterUserId, string targetRole);
}
