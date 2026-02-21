using Microsoft.AspNetCore.Authorization;

namespace Connapse.Identity.Authorization;

public class ScopeRequirement(string scope) : IAuthorizationRequirement
{
    public string Scope { get; } = scope;
}
