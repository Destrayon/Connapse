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
    /// Each built-in provider's setup, read through providers.status as GET /api/v1/providers reads it;
    /// none when the caller may not see provider setup, whom the page doesn't let in anyway.
    /// </summary>
    public static async Task<IReadOnlyList<Connapse.Core.ProviderSetup>> ReadAsync(
        this Connapse.Application.Providers.GetProviderStatus status, AuthenticationStateProvider authentication) =>
        (await status.RunAsync(await authentication.OperationContextAsync())).Result ?? [];

    /// <summary>The custom providers, read through providers.custom.list as GET /api/v1/providers/custom reads them; none when the caller may not list them.</summary>
    public static async Task<IReadOnlyList<Connapse.Core.CustomProvider>> ReadAsync(
        this Connapse.Application.Providers.ListCustomProviders list, AuthenticationStateProvider authentication) =>
        (await list.RunAsync(await authentication.OperationContextAsync())).Result?.Select(l => l.Provider).ToList() ?? [];

    /// <summary>The stored AWS credential, read through providers.aws.access.get; none when the caller may not read it.</summary>
    public static async Task<Connapse.Application.Providers.AwsAccessView> ReadAsync(
        this Connapse.Application.Providers.GetAwsAccess read, AuthenticationStateProvider authentication) =>
        (await read.RunAsync(await authentication.OperationContextAsync())).Result ?? new(null, null);

    /// <summary>The stored GitHub App, read through providers.github.app.get; none when the caller may not read it.</summary>
    public static async Task<Connapse.Application.Providers.GitHubAppView> ReadAsync(
        this Connapse.Application.Providers.GetGitHubApp read, AuthenticationStateProvider authentication) =>
        (await read.RunAsync(await authentication.OperationContextAsync())).Result ?? new(null, null);

    /// <summary>A category's settings in effect, without secrets, read through settings.get; null when they can't be read.</summary>
    public static async Task<object?> InEffectAsync(
        this Connapse.Application.Settings.GetSettings get, AuthenticationStateProvider authentication, string category) =>
        (await get.RunAsync(await authentication.OperationContextAsync(), category)).Result?.Settings;

    /// <summary>
    /// Whether a category's settings in effect are now exactly <paramref name="settings"/>, secrets aside since
    /// settings.get never returns them; null when they can't be read, which is not a no.
    /// </summary>
    public static async Task<bool?> InEffectMatchesAsync(
        this Connapse.Application.Settings.GetSettings get, AuthenticationStateProvider authentication, string category, object settings)
    {
        var found = Connapse.Application.Settings.SettingsCategories.Find(category)
            ?? Connapse.Application.Settings.SettingsCategories.FindProviderOwned(category)?.Category;
        if (found is null || await get.InEffectAsync(authentication, category) is not { } inEffect)
            return null;
        return System.Text.Json.JsonSerializer.Serialize(inEffect, found.Type)
            == System.Text.Json.JsonSerializer.Serialize(found.WithoutSecrets(settings), found.Type);
    }

    /// <summary>
    /// Tests a connection through settings.testConnection with the settings on the page and any key typed on
    /// it; a saved key is used only at the address it was saved for. Whether it connected, and what the page says.
    /// </summary>
    public static async Task<(bool Success, string Message)> TestAsync(
        this Connapse.Application.Settings.TestConnection test, AuthenticationStateProvider authentication, string category, object settings, int timeoutSeconds)
    {
        var tested = await test.RunAsync(await authentication.OperationContextAsync(), new Connapse.Application.Settings.ConnectionTestRequest(
            category, System.Text.Json.JsonSerializer.SerializeToElement(settings, settings.GetType()), timeoutSeconds));
        return tested.Succeeded ? (tested.Result!.Success, tested.Result.Message) : (false, $"Test failed: {tested.Detail}");
    }

    /// <summary>
    /// Turns on cross-model search through settings.get and settings.update, leaving the rest of the search
    /// settings, its key included, as stored. Whether it saved, and what failed when it didn't.
    /// </summary>
    public static async Task<(bool Saved, string? Detail)> EnableCrossModelSearchAsync(
        this Connapse.Application.Settings.UpdateSettings update, Connapse.Application.Settings.GetSettings get, AuthenticationStateProvider authentication)
    {
        var context = await authentication.OperationContextAsync();
        var read = await get.RunAsync(context, "search");
        if (!read.Succeeded)
            return (false, read.Detail);
        var search = (Connapse.Core.SearchSettings)read.Result!.Settings;
        search.EnableCrossModelSearch = true;
        var saved = await update.RunAsync(context, "search", System.Text.Json.JsonSerializer.SerializeToElement(search));
        return (saved.Succeeded, saved.Detail);
    }
}
