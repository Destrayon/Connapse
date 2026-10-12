using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.Atlassian;

namespace Connapse.Web.Services;

public enum AtlassianLinkAppSaveOutcome { Saved, MissingFields, Rejected, Unreachable }

public enum AtlassianLinkAppRemoveOutcome { Removed, NotStored, ConfirmMismatch }

/// <param name="Detail">Atlassian's answer when it refused the app or could not be asked; never the secret.</param>
public sealed record AtlassianLinkAppSaveResult(AtlassianLinkAppSaveOutcome Outcome, string? Detail = null);

/// <summary>
/// The Atlassian linking app's lifecycle, shared by the Providers page and the REST API so both
/// check with Atlassian before saving and write the same audit events: <c>provider.credential.checked</c>,
/// <c>provider.credential.saved</c>, <c>provider.sign_in.enabled</c>/<c>disabled</c> and
/// <c>provider.credential.deleted</c>. Each carries the client ID only, never the secret.
/// </summary>
public sealed class AtlassianLinkAppService(
    IProviderCredentialStore credentials,
    AtlassianUserSignIn signIn,
    IAtlassianIdentityLinkReader links,
    IAuditLogger audit,
    ILogger<AtlassianLinkAppService> logger)
{
    public const string Provider = "atlassian";

    public Task<AtlassianLinkAppRegistration?> GetAsync(CancellationToken ct = default) =>
        credentials.GetAtlassianLinkAppAsync(ct);

    /// <summary>How many people have linked an Atlassian account, or null when they can't be counted.</summary>
    public async Task<int?> CountLinkedPeopleAsync(CancellationToken ct = default)
    {
        try
        {
            return await links.CountLinksAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not count linked Atlassian accounts");
            return null;
        }
    }

    /// <summary>
    /// Asks Atlassian whether it accepts the client ID and secret and, only when it does, stores them
    /// in place of any app already stored. Saving also turns sign-in back on.
    /// </summary>
    /// <param name="callbackUrl">This server's sign-in callback, the redirect URI the check sends.</param>
    public async Task<AtlassianLinkAppSaveOutcome> SaveAsync(
        string? clientId, string? clientSecret, string callbackUrl, Guid? userId, CancellationToken ct = default) =>
        (await CheckAndSaveAsync(clientId, clientSecret, callbackUrl, userId, ct)).Outcome;

    /// <summary><see cref="SaveAsync"/>, keeping Atlassian's answer for a refusal or a failed check.</summary>
    public async Task<AtlassianLinkAppSaveResult> CheckAndSaveAsync(
        string? clientId, string? clientSecret, string callbackUrl, Guid? userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return new(AtlassianLinkAppSaveOutcome.MissingFields);

        string trimmedId = clientId.Trim();
        var check = await signIn.CheckAppWithDetailAsync(trimmedId, clientSecret, callbackUrl, ct);
        await audit.LogAsync("provider.credential.checked", "provider", Provider,
            new { ClientId = trimmedId, Result = check.Result.ToString(), check.Detail }, ct);

        if (check.Result == AtlassianLinkAppCheck.Rejected)
            return new(AtlassianLinkAppSaveOutcome.Rejected, check.Detail);
        if (check.Result == AtlassianLinkAppCheck.Unknown)
            return new(AtlassianLinkAppSaveOutcome.Unreachable, check.Detail);

        await credentials.SaveAtlassianLinkAppAsync(trimmedId, clientSecret.Trim(), userId, DateTime.UtcNow, ct);
        await credentials.RememberAtlassianCallbackUrlAsync(trimmedId, callbackUrl, ct);
        // The same event GitHub's App writes. The client ID only: never the secret.
        await audit.LogAsync("provider.credential.saved", "provider", Provider,
            new { ClientId = trimmedId, Via = "manual" }, ct);
        return new(AtlassianLinkAppSaveOutcome.Saved);
    }

    /// <summary>Checks the stored app with Atlassian again and records the answer.</summary>
    /// <returns>Null when no app is stored.</returns>
    public async Task<AtlassianLinkAppCheck?> RecheckAsync(string callbackUrl, CancellationToken ct = default) =>
        (await RecheckWithDetailAsync(callbackUrl, ct))?.Result;

    /// <summary><see cref="RecheckAsync"/>, keeping Atlassian's answer as the result's detail.</summary>
    public async Task<AtlassianLinkAppCheckResult?> RecheckWithDetailAsync(string callbackUrl, CancellationToken ct = default)
    {
        var app = await credentials.GetAtlassianLinkAppAsync(ct);
        var check = await signIn.CheckStoredAppWithDetailAsync(callbackUrl, ct);
        await audit.LogAsync("provider.credential.checked", "provider", Provider,
            new { app?.ClientId, Result = check?.Result.ToString() ?? "NotStored", check?.Detail }, ct);

        // An answer from Atlassian proves the callback URL is one it takes, so the daily check reuses it.
        if (app is not null && check is { Result: not AtlassianLinkAppCheck.Unknown })
            await credentials.RememberAtlassianCallbackUrlAsync(app.ClientId, callbackUrl, ct);
        return check;
    }

    /// <summary>
    /// The daily check: rechecks the stored app with the callback URL it was last used with, so a
    /// secret revoked in the developer console shows as rejected before someone's sign-in fails.
    /// Skipped, returning null, when no app is stored, sign-in is off, or no callback URL is known yet.
    /// </summary>
    public async Task<AtlassianLinkAppCheckResult?> ScheduledRecheckAsync(CancellationToken ct = default)
    {
        var app = await credentials.GetAtlassianLinkAppAsync(ct);
        if (app is null || !app.SignInEnabled)
            return null;
        if (string.IsNullOrWhiteSpace(app.CallbackUrl))
        {
            logger.LogInformation(
                "Atlassian linking app check skipped: no callback URL recorded yet. Choose Test connection on the linking app on the Providers page once.");
            return null;
        }

        return await RecheckWithDetailAsync(app.CallbackUrl, ct);
    }

    /// <summary>Turns Atlassian sign-in on or off without touching the stored app.</summary>
    /// <returns>False when no app is stored.</returns>
    public async Task<bool> SetSignInEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        var app = await credentials.GetAtlassianLinkAppAsync(ct);
        if (app is null || !await credentials.SetAtlassianSignInEnabledAsync(enabled, ct))
            return false;

        await audit.LogAsync(enabled ? "provider.sign_in.enabled" : "provider.sign_in.disabled", "provider", Provider,
            new { app.ClientId }, ct);
        return true;
    }

    /// <summary>
    /// Removes the stored app when <paramref name="confirmClientId"/> is its client ID, the value the
    /// page asks an administrator to type. People who linked keep their links.
    /// </summary>
    public async Task<AtlassianLinkAppRemoveOutcome> RemoveAsync(string? confirmClientId, CancellationToken ct = default)
    {
        var app = await credentials.GetAtlassianLinkAppAsync(ct);
        if (app is null)
            return AtlassianLinkAppRemoveOutcome.NotStored;
        if (!string.Equals(confirmClientId?.Trim(), app.ClientId, StringComparison.Ordinal))
            return AtlassianLinkAppRemoveOutcome.ConfirmMismatch;

        await credentials.DeleteAsync(Provider, ct);
        await audit.LogAsync("provider.credential.deleted", "provider", Provider, new { app.ClientId }, ct);
        return AtlassianLinkAppRemoveOutcome.Removed;
    }
}
