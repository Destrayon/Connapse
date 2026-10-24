using Connapse.Application;
using Microsoft.AspNetCore.Components.Authorization;

namespace Connapse.Web.Components;

/// <summary>How a page names its caller to an operation: the signed-in user, on the UI surface.</summary>
public static class OperationCallers
{
    public static async Task<OperationContext> OperationContextAsync(this AuthenticationStateProvider authentication) =>
        new((await authentication.GetAuthenticationStateAsync()).User, OperationSurface.Ui);

    /// <summary>The result when the attempt succeeded; otherwise its detail goes to <paramref name="failed"/>, the page's own alert, and <paramref name="fallback"/> is shown instead.</summary>
    public static TResult ResultOr<TResult, TStatus>(this Attempt<TResult, TStatus> attempt, Action<string> failed, TResult fallback)
        where TStatus : struct, Enum
    {
        if (attempt.Succeeded)
            return attempt.Result!;
        failed(attempt.Detail ?? "That didn't work.");
        return fallback;
    }
}
