using System.Security.Claims;
using Connapse.Identity.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Connapse.Identity.Services;

public class ConnapseUserClaimsPrincipalFactory(
    UserManager<ConnapseUser> userManager,
    RoleManager<ConnapseRole> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ConnapseUser, ConnapseRole>(userManager, roleManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ConnapseUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);

        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            identity.AddClaim(new Claim("DisplayName", user.DisplayName));
        }

        return identity;
    }
}
