using Microsoft.AspNetCore.Authentication;

namespace Connapse.Identity.Authentication;

public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string TokenPrefix = "cnp_";
}
