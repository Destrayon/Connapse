using Connapse.Identity.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Connapse.Web.Services.GenericApi;

/// <summary>
/// Who sees the generic connapse_api_* tools: a caller whose token allows the scope of at least one
/// operation the tool could call. The tool still checks each call's own scope.
/// </summary>
public static class GenericApiPolicies
{
    public const string Search = "Mcp:ApiSearch";
    public const string Read = "Mcp:ApiRead";
    public const string Write = "Mcp:ApiWrite";

    public static AuthorizationBuilder AddGenericApiPolicies(this AuthorizationBuilder builder) => builder
        .AddPolicy(Search, policy => policy.RequireAuthenticatedUser().RequireAssertion(c => Allows(c, reads: true) || Allows(c, reads: false)))
        .AddPolicy(Read, policy => policy.RequireAuthenticatedUser().RequireAssertion(c => Allows(c, reads: true)))
        .AddPolicy(Write, policy => policy.RequireAuthenticatedUser().RequireAssertion(c => Allows(c, reads: false)));

    private static bool Allows(AuthorizationHandlerContext context, bool reads) =>
        ApiSpec.AllowsAny(scope => TokenScopes.Allows(context.User, scope), reads);
}
