using Connapse.Identity.Data.Entities;
using Microsoft.AspNetCore.Identity;

namespace Connapse.Identity.Services;

/// <summary>
/// Default policy for single-tenant mode: checks ASP.NET Identity roles.
/// Only users with the "Owner" Identity role can invite Admins.
/// </summary>
public class DefaultInviteAuthorizationPolicy(UserManager<ConnapseUser> userManager) : IInviteAuthorizationPolicy
{
    public async Task<bool> CanInviteWithRoleAsync(Guid inviterUserId, string targetRole)
    {
        if (targetRole != "Admin")
            return true;

        var inviter = await userManager.FindByIdAsync(inviterUserId.ToString());
        return inviter is not null && await userManager.IsInRoleAsync(inviter, "Owner");
    }
}
