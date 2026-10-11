using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.Atlassian;

namespace Connapse.Web.Services;

public enum AtlassianLinkAppSaveOutcome { Saved, MissingFields, Rejected, Unreachable }

public enum AtlassianLinkAppRemoveOutcome { Removed, NotStored, ConfirmMismatch }

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
        string? clientId, string? clientSecret, string callbackUrl, Guid? userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return AtlassianLinkAppSaveOutcome.MissingFields;

        string trimmedId = clientId.Trim();
        var result = await signIn.CheckAppAsync(trimmedId, clientSecret, callbackUrl, ct);
        await audit.LogAsync("provider.credential.checked", "provider", Provider,
            new { ClientId = trimmedId, Result = result.ToString() }, ct);

        if (result == AtlassianLinkAppCheck.Rejected)
            return AtlassianLinkAppSaveOutcome.Rejected;
        if (result == AtlassianLinkAppCheck.Unknown)
            return AtlassianLinkAppSaveOutcome.Unreachable;

        await credentials.SaveAtlassianLinkAppAsync(trimmedId, clientSecret.Trim(), userId, DateTime.UtcNow, ct);
        // The same event GitHub's App writes. The client ID only: never the secret.
        await audit.LogAsync("provider.credential.saved", "provider", Provider,
            new { ClientId = trimmedId, Via = "manual" }, ct);
        return AtlassianLinkAppSaveOutcome.Saved;
    }

    /// <summary>Checks the stored app with Atlassian again and records the answer.</summary>
    /// <returns>Null when no app is stored.</returns>
    public async Task<AtlassianLinkAppCheck?> RecheckAsync(string callbackUrl, CancellationToken ct = default)
    {
        var app = await credentials.GetAtlassianLinkAppAsync(ct);
        var result = await signIn.CheckStoredAppAsync(callbackUrl, ct);
        await audit.LogAsync("provider.credential.checked", "provider", Provider,
            new { app?.ClientId, Result = result?.ToString() ?? "NotStored" }, ct);
        return result;
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
