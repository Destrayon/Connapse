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

    /// <summary>What a page says after an action: <paramref name="success"/> when the attempt succeeded, otherwise its detail.</summary>
    public static string Says<TResult, TStatus>(this Attempt<TResult, TStatus> attempt, string success)
        where TStatus : struct, Enum =>
        attempt.Succeeded ? success : attempt.Detail ?? "That didn't work.";

    /// <summary>
    /// The caller's account links, read through links.list as GET /api/v1/auth/links reads them, except that
    /// custom providers' status is left to each provider's card, which asks its own server.
    /// </summary>
    public static async Task<Attempt<Connapse.Application.Links.AccountLinks, Connapse.Application.Links.AccountLinkReadStatus>> MineAsync(
        this Connapse.Application.Links.AccountLinkOperations operations, AuthenticationStateProvider authentication) =>
        await operations.List.RunAsync(await authentication.OperationContextAsync(),
            new Connapse.Application.Links.ListAccountLinksQuery(CustomStatus: false));

    /// <summary>Unlinks one of the signed-in person's accounts through links.unlink, from a component given the authentication state.</summary>
    public static async Task<Attempt<Connapse.Application.Links.UnlinkAccountRequest, Connapse.Application.Links.AccountUnlinkStatus>> RunAsync(
        this Connapse.Application.Links.UnlinkAccount unlink, AuthenticationState authentication, Connapse.Application.Links.UnlinkAccountRequest request) =>
        await unlink.RunAsync(new OperationContext(authentication.User, OperationSurface.Ui), request);

    /// <summary>
    /// The sources already on a connection, read through sources.list, so a dialog can leave out what
    /// is already added; none when the caller may not list sources, who can't add one either.
    /// </summary>
    public static async Task<IReadOnlyList<Connapse.Core.Source>> OnConnectionAsync(
        this Connapse.Application.Sources.SourceOperations operations, AuthenticationStateProvider authentication, Guid connectionId) =>
        (await operations.List.RunAsync(await authentication.OperationContextAsync(),
            new Connapse.Application.Sources.ListSourcesQuery(ConnectionId: connectionId))).Result?.Items ?? [];

    /// <summary>
    /// Saves a settings category through settings.update, as PUT /api/settings/{category} does: the category's
    /// rules, its stored key and the embedding index all apply. The page holds every value, keys included, so a
    /// key it sends empty is one it cleared, as on a provider switch. Whether it saved, and what the page says.
    /// </summary>
    public static async Task<(bool Saved, string Message)> SaveAsync(
        this Connapse.Application.Settings.UpdateSettings update, AuthenticationStateProvider authentication, string category, object settings)
    {
        var saved = await update.RunAsync(await authentication.OperationContextAsync(), category,
            System.Text.Json.JsonSerializer.SerializeToElement(settings, settings.GetType()), Connapse.Application.Settings.UnsentSecrets.Clear);
        return saved.Succeeded
            ? (true, $"{category} settings saved successfully!")
            : (false, $"Failed to save {category} settings: {saved.Detail}");
    }
}
