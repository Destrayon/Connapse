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

    /// <summary>
    /// The sources already on a connection, read through sources.list, so a dialog can leave out what
    /// is already added; none when the caller may not list sources, who can't add one either.
    /// </summary>
    public static async Task<IReadOnlyList<Connapse.Core.Source>> OnConnectionAsync(
        this Connapse.Application.Sources.SourceOperations operations, AuthenticationStateProvider authentication, Guid connectionId) =>
        (await operations.List.RunAsync(await authentication.OperationContextAsync(),
            new Connapse.Application.Sources.ListSourcesQuery(ConnectionId: connectionId))).Result?.Items ?? [];
}
