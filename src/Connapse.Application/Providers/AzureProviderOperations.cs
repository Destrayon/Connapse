using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using Connapse.Storage.Settings;

namespace Connapse.Application.Providers;

/// <summary>Why an Azure setup action didn't succeed.</summary>
public enum AzureSetupStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(400, "invalid-azure-setup", "Invalid Azure setup")]
    Invalid,

    [Failure(400, "azure-credential-refused", "Entra refused the credential")]
    Refused,

    [Failure(503, "enforcement-not-on", "Per-user enforcement isn't on")]
    NotEnforcing,
}

/// <summary>
/// Connapse's Azure access identity: a certificate app (client id and certificate path), a user-assigned managed
/// identity (its client id), or the host's own managed identity, in a tenant, with the subscription per-user
/// filtering reads role assignments from. A null certificate password keeps the stored one for the same certificate.
/// </summary>
public sealed record AzureAccessRequest(
    string TenantId, string? SubscriptionId = null, string? ClientId = null, string? ClientCertificatePath = null,
    string? ClientCertificatePassword = null, string? UserAssignedManagedIdentityClientId = null,
    bool UseHostManagedIdentity = false, string? ManagedIdentityPrincipalId = null)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"TenantId = {TenantId}, SubscriptionId = {SubscriptionId}, ClientId = {ClientId}, "
            + $"ClientCertificatePath = {ClientCertificatePath}, ClientCertificatePassword = {(ClientCertificatePassword is null ? "null" : "***")}, "
            + $"UserAssignedManagedIdentityClientId = {UserAssignedManagedIdentityClientId}, UseHostManagedIdentity = {UseHostManagedIdentity}");
        return true;
    }
}

/// <summary>
/// The Entra application people sign in through: its tenant, client id, redirect address and certificate.
/// <paramref name="AdminConsentPending"/> records that the access identity's Graph permissions still await an
/// administrator's consent; send false once it is granted. A null certificate password keeps the stored one for the
/// same certificate.
/// </summary>
public sealed record AzurePermissionsRequest(
    string? TenantId = null, string? ClientId = null, string? RedirectUri = null, string? ClientCertificatePath = null,
    string? ClientCertificatePassword = null, bool AdminConsentPending = false)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"TenantId = {TenantId}, ClientId = {ClientId}, RedirectUri = {RedirectUri}, "
            + $"ClientCertificatePath = {ClientCertificatePath}, ClientCertificatePassword = {(ClientCertificatePassword is null ? "null" : "***")}, "
            + $"AdminConsentPending = {AdminConsentPending}");
        return true;
    }
}

/// <summary>
/// Saved settings, without the certificate password, and whether they are in effect yet: false means they apply after
/// a restart. <see cref="AdminConsentUrl"/> is set while admin consent is pending and the access identity is an app: an
/// administrator opens it in a browser, and GET /api/v1/providers shows when the consent is recorded.
/// </summary>
public sealed record AzureSettingsSaved<T>(T Settings, bool Applied, string? AdminConsentUrl = null);

/// <summary>
/// Sets Connapse's Azure access identity. Entra is asked for a token with it first, and nothing is saved unless it
/// gives one, so a wrong tenant, client id or certificate never replaces a working identity.
/// </summary>
[Operation("providers.azure.access", Scope = "admin:settings", Risk = RiskTier.Credential, Mcp = McpExposure.Generic)]
public sealed class SetAzureAccess(
    ISettingsStore store, ISettingsReloader reloader, IConfiguredSettings configured, IAzureBlobDiscovery discovery,
    IAzureHostIdentity hostIdentity, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<AzureSettingsSaved<AzureProviderSettings>, AzureSetupStatus>> RunAsync(
        OperationContext context, AzureAccessRequest request, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<AzureSettingsSaved<AzureProviderSettings>, AzureSetupStatus>.Fail(AzureSetupStatus.Forbidden, AzureRules.Refused);

        AzureProviderSettings stored = configured.Current<AzureProviderSettings>();
        var settings = new AzureProviderSettings
        {
            TenantId = AzureRules.Blank(request.TenantId),
            SubscriptionId = AzureRules.Blank(request.SubscriptionId),
            ClientId = AzureRules.Blank(request.ClientId),
            ClientCertificatePath = AzureRules.Blank(request.ClientCertificatePath),
            ClientCertificatePassword = AzureRules.Password(request.ClientCertificatePassword, request.ClientCertificatePath,
                stored.ClientCertificatePath, stored.ClientCertificatePassword),
            UserAssignedManagedIdentityClientId = AzureRules.Blank(request.UserAssignedManagedIdentityClientId),
            UseHostManagedIdentity = request.UseHostManagedIdentity,
            ManagedIdentityPrincipalId = AzureRules.Blank(request.ManagedIdentityPrincipalId),
        };
        if (settings.TenantId is null)
            return Attempt<AzureSettingsSaved<AzureProviderSettings>, AzureSetupStatus>.Fail(AzureSetupStatus.Invalid,
                "Enter the tenant id.", "tenantId");

        // The host's identity is in one tenant, and a managed-identity token can't tell whether the tenant typed
        // matches it; the host already said which it is.
        if (AzureCredentialChainFactory.IsHostManagedIdentity(settings)
            && await hostIdentity.DetectAsync(ct: ct) is { } host
            && !string.Equals(host.TenantId, settings.TenantId, StringComparison.OrdinalIgnoreCase))
            return Attempt<AzureSettingsSaved<AzureProviderSettings>, AzureSetupStatus>.Fail(AzureSetupStatus.Invalid,
                $"This host's managed identity is in tenant {host.TenantId}, not {settings.TenantId}. Nothing was changed; enter that tenant.",
                "tenantId");

        AzureProbe<string> check = await discovery.CheckAccessAsync(settings, ct);
        if (!check.Succeeded)
            return Attempt<AzureSettingsSaved<AzureProviderSettings>, AzureSetupStatus>.Fail(AzureSetupStatus.Refused,
                AzureRules.CheckRefused("access", check));

        return Attempt<AzureSettingsSaved<AzureProviderSettings>, AzureSetupStatus>.Succeed(
            await AzureRules.SaveAccessAsync(store, reloader, audit, settings, ct));
    }
}

/// <summary>
/// Clears Connapse's Azure access identity. The app registration and its role assignments stay in Azure until an
/// administrator removes them there.
/// </summary>
[Operation("providers.azure.access.reset", Scope = "admin:settings", Risk = RiskTier.Destructive, Mcp = McpExposure.Generic)]
public sealed class ResetAzureAccess(ISettingsStore store, ISettingsReloader reloader, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<AzureSettingsSaved<AzureProviderSettings>, AzureSetupStatus>> RunAsync(OperationContext context, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<AzureSettingsSaved<AzureProviderSettings>, AzureSetupStatus>.Fail(AzureSetupStatus.Forbidden, AzureRules.Refused);

        return Attempt<AzureSettingsSaved<AzureProviderSettings>, AzureSetupStatus>.Succeed(
            await AzureRules.SaveAccessAsync(store, reloader, audit, new AzureProviderSettings(), ct));
    }
}

/// <summary>
/// Saves the Entra application people sign in through. Once it is complete, Entra is asked for a token with its
/// certificate, and per-user Azure permission enforcement is switched on before anything is saved; it can't be
/// switched off again. While admin consent is pending, the result carries the consent link.
/// </summary>
[Operation("providers.azure.permissions", Scope = "admin:settings", Risk = RiskTier.Destructive, Mcp = McpExposure.Generic)]
public sealed class SetAzurePermissions(
    ISettingsStore store, ISettingsReloader reloader, IConfiguredSettings configured, IAzureBlobDiscovery discovery,
    IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<AzureSettingsSaved<AzureAdSignInSettings>, AzureSetupStatus>> RunAsync(
        OperationContext context, AzurePermissionsRequest request, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<AzureSettingsSaved<AzureAdSignInSettings>, AzureSetupStatus>.Fail(AzureSetupStatus.Forbidden, AzureRules.Refused);

        AzureAdSignInSettings stored = configured.Current<AzureAdSignInSettings>();
        var settings = new AzureAdSignInSettings
        {
            TenantId = AzureRules.Blank(request.TenantId),
            ClientId = AzureRules.Blank(request.ClientId),
            RedirectUri = AzureRules.Blank(request.RedirectUri),
            ClientCertificatePath = AzureRules.Blank(request.ClientCertificatePath),
            ClientCertificatePassword = AzureRules.Password(request.ClientCertificatePassword, request.ClientCertificatePath,
                stored.ClientCertificatePath, stored.ClientCertificatePassword),
            AdminConsentPending = request.AdminConsentPending,
        };
        if (settings.RedirectUri is { } redirect
            && !(Uri.TryCreate(redirect, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)))
            return Attempt<AzureSettingsSaved<AzureAdSignInSettings>, AzureSetupStatus>.Fail(AzureSetupStatus.Invalid,
                "The redirect address must be a full http or https address.", "redirectUri");

        if (settings.IsConfigured)
        {
            AzureProbe<string> check = await discovery.CheckAccessAsync(AzureRules.SignInIdentity(settings), ct);
            if (!check.Succeeded)
                return Attempt<AzureSettingsSaved<AzureAdSignInSettings>, AzureSetupStatus>.Fail(AzureSetupStatus.Refused,
                    AzureRules.CheckRefused("sign-in", check));

            // Latched before the sign-in settings, never after: sign-in stored with filtering off would show every
            // user every Azure document, while enforcement on with sign-in not yet stored only denies.
            if (!configured.Current<PermissionEnforcementSettings>().AzureEnforcing
                && !await AzureRules.LatchEnforcementAsync(store, reloader, configured, ct))
                return Attempt<AzureSettingsSaved<AzureAdSignInSettings>, AzureSetupStatus>.Fail(AzureSetupStatus.NotEnforcing,
                    "Per-user enforcement for Azure couldn't be switched on in this process, so the sign-in application wasn't saved. "
                    + "Restart Connapse and save again.");
        }

        AzureSettingsSaved<AzureAdSignInSettings> saved = await AzureRules.SaveSignInAsync(store, reloader, audit, settings, ct);
        return Attempt<AzureSettingsSaved<AzureAdSignInSettings>, AzureSetupStatus>.Succeed(saved with
        {
            AdminConsentUrl = settings.AdminConsentPending ? AzureRules.ConsentUrl(configured.Current<AzureProviderSettings>(), settings) : null,
        });
    }
}

/// <summary>
/// Clears the recorded sign-in application, for one removed in Entra. Per-user enforcement stays on, so searches of
/// Azure sources return nothing for everyone until the application is saved again.
/// </summary>
[Operation("providers.azure.permissions.reset", Scope = "admin:settings", Risk = RiskTier.Destructive, Mcp = McpExposure.Generic)]
public sealed class ResetAzurePermissions(ISettingsStore store, ISettingsReloader reloader, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<AzureSettingsSaved<AzureAdSignInSettings>, AzureSetupStatus>> RunAsync(OperationContext context, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<AzureSettingsSaved<AzureAdSignInSettings>, AzureSetupStatus>.Fail(AzureSetupStatus.Forbidden, AzureRules.Refused);

        return Attempt<AzureSettingsSaved<AzureAdSignInSettings>, AzureSetupStatus>.Succeed(
            await AzureRules.SaveSignInAsync(store, reloader, audit, new AzureAdSignInSettings(), ct));
    }
}

/// <summary>Every Azure setup operation in one place, for the providers page.</summary>
public sealed class AzureProviderOperations(
    SetAzureAccess access, ResetAzureAccess resetAccess, SetAzurePermissions permissions, ResetAzurePermissions resetPermissions)
{
    public SetAzureAccess Access { get; } = access;

    public ResetAzureAccess ResetAccess { get; } = resetAccess;

    public SetAzurePermissions Permissions { get; } = permissions;

    public ResetAzurePermissions ResetPermissions { get; } = resetPermissions;
}

/// <summary>The rules the Azure setup operations share.</summary>
internal static class AzureRules
{
    public const string Provider = "azure";

    public const string Refused = AwsRules.Refused;

    public const string AccessCategory = "azure";

    public const string SignInCategory = "azuread";

    /// <summary>Where Entra sends a person back after sign-in; the sign-in redirect address ends with it.</summary>
    public const string CallbackPath = "/api/v1/auth/cloud/azure/callback";

    /// <summary>The providers page path the setup script registers as the access app's consent reply address.</summary>
    public const string ConsentLandingPath = "/admin/providers/azure";

    public static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The password given, or the stored one when none is given and the certificate is the stored one.</summary>
    public static string? Password(string? given, string? path, string? storedPath, string? storedPassword) =>
        given is not null ? Blank(given)
        : Blank(path) is { } same && string.Equals(same, Blank(storedPath), StringComparison.Ordinal) ? storedPassword
        : null;

    /// <summary>The sign-in application in the shape the credential check takes: it authenticates with a certificate
    /// exactly as the access app does.</summary>
    public static AzureProviderSettings SignInIdentity(AzureAdSignInSettings signIn) => new()
    {
        TenantId = signIn.TenantId,
        ClientId = signIn.ClientId,
        ClientCertificatePath = signIn.ClientCertificatePath,
        ClientCertificatePassword = signIn.ClientCertificatePassword,
    };

    /// <summary>What to say when Entra won't accept a credential about to be stored.</summary>
    public static string CheckRefused(string which, AzureProbe<string> check) => check.Outcome switch
    {
        AzureProbeOutcome.Denied =>
            $"Entra refused the {which} credential, so nothing was changed. {check.Detail} If you just "
            + "ran the script, Entra may still be registering the certificate — wait a minute and save again.",
        AzureProbeOutcome.Unusable =>
            $"The {which} credential cannot be used from this host, so nothing was changed: {check.Detail} "
            + "For a certificate, check the path and that Connapse can read the file; for a managed identity, "
            + "that this host has one.",
        _ => $"Could not confirm the {which} credential with Azure, so nothing was changed. {check.Detail}",
    };

    /// <summary>
    /// The admin-consent link for the access app, or null for a managed identity (its grants are app roles, not
    /// consent). The reply address is the providers page on the host the sign-in redirect address names.
    /// </summary>
    public static string? ConsentUrl(AzureProviderSettings access, AzureAdSignInSettings signIn)
    {
        if (Blank(access.TenantId) is not { } tenant || Blank(access.ClientId) is not { } app)
            return null;
        string url = $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/adminconsent?client_id={Uri.EscapeDataString(app)}";
        if (signIn.RedirectUri is { } redirect && redirect.EndsWith(CallbackPath, StringComparison.OrdinalIgnoreCase))
            url += $"&redirect_uri={Uri.EscapeDataString(redirect[..^CallbackPath.Length] + ConsentLandingPath)}";
        return url;
    }

    public static async Task<AzureSettingsSaved<AzureProviderSettings>> SaveAccessAsync(
        ISettingsStore store, ISettingsReloader reloader, IAuditLogger audit, AzureProviderSettings settings, CancellationToken ct)
    {
        await store.SaveAsync(AccessCategory, settings, ct);
        bool applied = Reload(reloader);
        await audit.LogAsync("provider.azure.saved", "provider", Provider,
            new { HasTenant = settings.TenantId is not null, HasSubscription = settings.SubscriptionId is not null }, ct);
        return new(settings with { ClientCertificatePassword = null }, applied);
    }

    public static async Task<AzureSettingsSaved<AzureAdSignInSettings>> SaveSignInAsync(
        ISettingsStore store, ISettingsReloader reloader, IAuditLogger audit, AzureAdSignInSettings settings, CancellationToken ct)
    {
        await store.SaveAsync(SignInCategory, settings, ct);
        bool applied = Reload(reloader);
        await audit.LogAsync("provider.azuread.saved", "provider", Provider, new { Configured = settings.IsConfigured }, ct);
        return new(settings with { ClientCertificatePassword = null }, applied);
    }

    /// <summary>
    /// Switches the Azure enforcement latch on and reports whether this process now enforces. A merge under the
    /// store's lock, never a replace, so the AWS latch is carried along: a latch only ever goes on.
    /// </summary>
    public static async Task<bool> LatchEnforcementAsync(ISettingsStore store, ISettingsReloader reloader, IConfiguredSettings configured, CancellationToken ct)
    {
        PermissionEnforcementSettings current = configured.Current<PermissionEnforcementSettings>();
        await store.UpdateAsync<PermissionEnforcementSettings>(PermissionEnforcementSettings.Category, stored => new PermissionEnforcementSettings
        {
            IsEnforcing = (stored?.IsEnforcing ?? false) || current.IsEnforcing,
            AzureEnforcing = true,
        }, ct);
        Reload(reloader);
        return configured.Current<PermissionEnforcementSettings>().AzureEnforcing;
    }

    /// <summary>Applies stored settings to this process; false when they apply only after a restart.</summary>
    private static bool Reload(ISettingsReloader reloader)
    {
        try
        {
            return reloader.Reload();
        }
        catch (Exception)
        {
            return false;
        }
    }
}
