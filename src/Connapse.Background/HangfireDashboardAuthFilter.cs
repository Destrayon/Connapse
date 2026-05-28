using Hangfire.Dashboard;
using Microsoft.AspNetCore.Authorization;

namespace Connapse.Background;

/// <summary>
/// Defers Hangfire dashboard authorization to ASP.NET's authorization system,
/// requiring the "RequireAdmin" policy (org Owner/Admin role).
/// Cloud edition may override via DI with a stricter filter (e.g., RequireSystemAdmin).
/// </summary>
public sealed class HangfireDashboardAuthFilter : IDashboardAuthorizationFilter
{
    private readonly IAuthorizationService _authService;

    public HangfireDashboardAuthFilter(IAuthorizationService authService)
    {
        _authService = authService;
    }

    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext.User?.Identity?.IsAuthenticated != true)
            return false;

        // Synchronous check — Hangfire's IDashboardAuthorizationFilter contract is sync.
        // .GetAwaiter().GetResult() is acceptable here: authorization handler is in-process,
        // short, non-async-await-blocking.
        var result = _authService.AuthorizeAsync(httpContext.User, null, "RequireAdmin")
            .GetAwaiter().GetResult();
        return result.Succeeded;
    }
}
