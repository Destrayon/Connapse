using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.CloudScope.RolesAnywhere;
using Connapse.Storage.Settings;

namespace Connapse.Application.Providers;

/// <summary>Why an AWS setup action didn't succeed.</summary>
public enum AwsSetupStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(400, "invalid-aws-setup", "Invalid AWS setup")]
    Invalid,

    [Failure(400, "aws-credential-refused", "AWS refused the credential")]
    Refused,

    [Failure(409, "aws-access-changed", "AWS access changed")]
    Changed,

    [Failure(503, "enforcement-not-on", "Per-user enforcement isn't on")]
    NotEnforcing,
}

/// <summary>
/// An IAM Roles Anywhere credential: the certificate, the three ARNs it is used with, their region and the
/// certificate's private key. A blank key keeps the stored one, which only works with the stored certificate;
/// <paramref name="Expected"/>, when given with a blank key, is the credential the caller last read, and the save is
/// refused if the stored one has changed since.
/// </summary>
public sealed record AwsAccessRequest(
    string CertificatePem, string TrustAnchorArn, string ProfileArn, string RoleArn, string Region,
    string? PrivateKeyPem = null, RolesAnywhereConfig? Expected = null)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"TrustAnchorArn = {TrustAnchorArn}, ProfileArn = {ProfileArn}, RoleArn = {RoleArn}, Region = {Region}, "
            + $"PrivateKeyPem = {(PrivateKeyPem is null ? "null" : "***")}");
        return true;
    }
}

/// <summary>The removed credential, if there was one, and the CloudShell command that deletes its AWS resources.</summary>
public sealed record AwsAccessReset(RolesAnywhereConfig? Removed, string? CleanupScript);

/// <summary>The customer's IAM Identity Center instance: its region, ARN and identity store id.</summary>
public sealed record IdentityCenterRequest(string Region, string InstanceArn, string IdentityStoreId);

/// <summary>Saved settings, and whether they are in effect yet: false means they apply after a restart.</summary>
public sealed record AwsSettingsSaved<T>(T Settings, bool Applied);

/// <summary>
/// The IAM Identity Center SAML application: Connapse's entity id and assertion address, which the application
/// registers, and Identity Center's entity id, sign-in address and signing certificate, from its metadata.
/// </summary>
public sealed record AwsPermissionsRequest(
    string? EntityId = null, string? AcsUrl = null, string? IdpEntityId = null,
    string? IdpSingleSignOnUrl = null, string? IdpSigningCertificate = null);

/// <summary>
/// Sets Connapse's AWS access to an IAM Roles Anywhere credential. AWS is asked for a session with it first, and
/// nothing is saved unless that works, so a bad certificate, ARN or region never replaces a working credential.
/// </summary>
[Operation("providers.aws.access", Scope = "admin:settings", Risk = RiskTier.Credential, Mcp = McpExposure.Generic)]
public sealed class SetAwsAccess(
    IProviderCredentialStore credentials, IRolesAnywhereSetupValidator validator, IAwsSession session,
    IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<RolesAnywhereConfig, AwsSetupStatus>> RunAsync(OperationContext context, AwsAccessRequest request, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<RolesAnywhereConfig, AwsSetupStatus>.Fail(AwsSetupStatus.Forbidden, AwsRules.Refused);

        var config = new RolesAnywhereConfig(
            request.CertificatePem?.Trim() ?? "", request.TrustAnchorArn?.Trim() ?? "", request.ProfileArn?.Trim() ?? "",
            request.RoleArn?.Trim() ?? "", request.Region?.Trim() ?? "");
        if (AwsRules.MissingAccessField(config) is { } missing)
            return Attempt<RolesAnywhereConfig, AwsSetupStatus>.Fail(AwsSetupStatus.Invalid, $"Enter the {missing.Name}.", missing.Field);
        if (!AwsRolesAnywhereSetup.IsValidRegion(config.Region))
            return Attempt<RolesAnywhereConfig, AwsSetupStatus>.Fail(AwsSetupStatus.Invalid, "Enter an AWS region, such as us-east-1.", "region");

        // The credential this save replaces. The save below is conditional on it, so a credential someone else saves
        // while AWS is checking this one is never overwritten.
        RolesAnywhereConfig? current;
        string? key = request.PrivateKeyPem?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            // A blank key keeps the stored one, so an ARN can change without a key that is never shown again.
            RolesAnywhereCredentialMaterial? stored;
            try
            {
                stored = await credentials.GetRolesAnywhereMaterialAsync(ConnapseAwsProvider, ct);
            }
            catch (ProviderCredentialUnavailableException)
            {
                // The stored key can't be decrypted, most often after the data protection keys changed.
                return Attempt<RolesAnywhereConfig, AwsSetupStatus>.Fail(AwsSetupStatus.Invalid,
                    "The stored private key can't be read. Enter it again.", "privateKeyPem");
            }
            if (stored is null)
                return Attempt<RolesAnywhereConfig, AwsSetupStatus>.Fail(AwsSetupStatus.Invalid,
                    "Enter the private key: there is no stored credential to keep it from.", "privateKeyPem");
            key = stored.PrivateKeyPem;
            current = stored.Config;
        }
        else
        {
            current = await credentials.GetRolesAnywhereAsync(ConnapseAwsProvider, ct);
        }
        // Keeping the current key with older ARNs would put back a credential someone else just replaced.
        if (request.Expected is not null && current != request.Expected)
            return Attempt<RolesAnywhereConfig, AwsSetupStatus>.Fail(AwsSetupStatus.Changed, AwsRules.AccessChanged);

        RolesAnywhereValidationResult result = await validator.ValidateAsync(config, key, ct);
        if (!result.Ok)
            return Attempt<RolesAnywhereConfig, AwsSetupStatus>.Fail(AwsSetupStatus.Refused,
                $"AWS did not accept this credential, so nothing was changed: {result.Error}");

        if (await credentials.ReplaceRolesAnywhereAsync(ConnapseAwsProvider, current, config, key, null, null, ct) is null)
            return Attempt<RolesAnywhereConfig, AwsSetupStatus>.Fail(AwsSetupStatus.Changed, AwsRules.AccessChanged);
        // The session held now came from the old credential; the next AWS call must use this one.
        session.Clear();
        await audit.LogAsync("provider.credential.saved", "provider", ConnapseAwsProvider,
            new { Method = "RolesAnywhere", config.RoleArn, config.Region }, ct);
        return Attempt<RolesAnywhereConfig, AwsSetupStatus>.Succeed(config);
    }

    private const string ConnapseAwsProvider = AwsRules.Provider;
}

/// <summary>
/// Removes Connapse's stored AWS credential. It stays valid in AWS until the returned cleanup command deletes its
/// resources or it expires. With none stored, Connapse reads S3 with whatever credentials its environment provides.
/// </summary>
[Operation("providers.aws.access.reset", Scope = "admin:settings", Risk = RiskTier.Destructive, Mcp = McpExposure.Generic)]
public sealed class ResetAwsAccess(IProviderCredentialStore credentials, IAwsSession session, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<AwsAccessReset, AwsSetupStatus>> RunAsync(OperationContext context, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<AwsAccessReset, AwsSetupStatus>.Fail(AwsSetupStatus.Forbidden, AwsRules.Refused);

        // Read before the delete, so the cleanup command can name the ARNs.
        // The delete is conditional on what was read, so a credential saved in between is neither removed nor
        // left out of the cleanup command.
        RolesAnywhereConfig? config = await credentials.GetRolesAnywhereAsync(AwsRules.Provider, ct);
        if (!await credentials.DeleteRolesAnywhereAsync(AwsRules.Provider, config, ct) && config is not null)
            return Attempt<AwsAccessReset, AwsSetupStatus>.Fail(AwsSetupStatus.Changed, AwsRules.AccessChanged);
        session.Clear();
        await audit.LogAsync("provider.credential.removed", "provider", AwsRules.Provider,
            new { Method = "RolesAnywhere", config?.RoleArn, config?.Region }, ct);

        string? cleanup = config is null
            ? null
            : AwsRolesAnywhereSetup.GenerateResetScript(config.TrustAnchorArn, config.ProfileArn, config.RoleArn, config.Region);
        return Attempt<AwsAccessReset, AwsSetupStatus>.Succeed(new AwsAccessReset(config, cleanup));
    }
}

/// <summary>Records the customer's IAM Identity Center instance, which per-user AWS permissions are resolved against.</summary>
[Operation("providers.aws.identityCenter", Scope = "admin:settings", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class SetIdentityCenter(ISettingsStore store, ISettingsReloader reloader, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<AwsSettingsSaved<IdentityCenterSettings>, AwsSetupStatus>> RunAsync(
        OperationContext context, IdentityCenterRequest request, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<AwsSettingsSaved<IdentityCenterSettings>, AwsSetupStatus>.Fail(AwsSetupStatus.Forbidden, AwsRules.Refused);

        var settings = new IdentityCenterSettings
        {
            Region = request.Region?.Trim() ?? "",
            InstanceArn = request.InstanceArn?.Trim() ?? "",
            IdentityStoreId = request.IdentityStoreId?.Trim() ?? "",
        };
        if (!AwsRolesAnywhereSetup.IsValidRegion(settings.Region))
            return Attempt<AwsSettingsSaved<IdentityCenterSettings>, AwsSetupStatus>.Fail(AwsSetupStatus.Invalid,
                "Enter the region the Identity Center instance is in, such as us-east-1.", "region");
        if (settings.InstanceArn.Length == 0)
            return Attempt<AwsSettingsSaved<IdentityCenterSettings>, AwsSetupStatus>.Fail(AwsSetupStatus.Invalid,
                "Enter the instance ARN.", "instanceArn");
        if (settings.IdentityStoreId.Length == 0)
            return Attempt<AwsSettingsSaved<IdentityCenterSettings>, AwsSetupStatus>.Fail(AwsSetupStatus.Invalid,
                "Enter the identity store id.", "identityStoreId");

        return Attempt<AwsSettingsSaved<IdentityCenterSettings>, AwsSetupStatus>.Succeed(
            await AwsRules.SaveIdentityCenterAsync(store, reloader, audit, settings, ct));
    }
}

/// <summary>
/// Clears the recorded Identity Center instance. Blanks are stored rather than the row removed, so values from
/// configuration don't come back in their place.
/// </summary>
[Operation("providers.aws.identityCenter.reset", Scope = "admin:settings", Risk = RiskTier.Destructive, Mcp = McpExposure.Generic)]
public sealed class ResetIdentityCenter(ISettingsStore store, ISettingsReloader reloader, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<AwsSettingsSaved<IdentityCenterSettings>, AwsSetupStatus>> RunAsync(OperationContext context, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<AwsSettingsSaved<IdentityCenterSettings>, AwsSetupStatus>.Fail(AwsSetupStatus.Forbidden, AwsRules.Refused);

        return Attempt<AwsSettingsSaved<IdentityCenterSettings>, AwsSetupStatus>.Succeed(
            await AwsRules.SaveIdentityCenterAsync(store, reloader, audit, new IdentityCenterSettings(), ct));
    }
}

/// <summary>
/// Saves the IAM Identity Center SAML application people sign in through. Once all five values are set, per-user
/// AWS permission enforcement is switched on first, and it can't be switched off again: clearing the application
/// later leaves searches of AWS sources returning nothing rather than everything.
/// </summary>
[Operation("providers.aws.permissions", Scope = "admin:settings", Risk = RiskTier.Destructive, Mcp = McpExposure.Generic)]
public sealed class SetAwsPermissions(
    ISettingsStore store, ISettingsReloader reloader, IConfiguredSettings configured, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<AwsSettingsSaved<SamlSignInSettings>, AwsSetupStatus>> RunAsync(
        OperationContext context, AwsPermissionsRequest request, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<AwsSettingsSaved<SamlSignInSettings>, AwsSetupStatus>.Fail(AwsSetupStatus.Forbidden, AwsRules.Refused);

        var settings = new SamlSignInSettings
        {
            EntityId = request.EntityId?.Trim() ?? "",
            AcsUrl = request.AcsUrl?.Trim() ?? "",
            IdpEntityId = request.IdpEntityId?.Trim() ?? "",
            IdpSingleSignOnUrl = request.IdpSingleSignOnUrl?.Trim() ?? "",
            IdpSigningCertificate = request.IdpSigningCertificate?.Trim() ?? "",
        };
        if (AwsRules.NotAnAddress(settings.AcsUrl))
            return Attempt<AwsSettingsSaved<SamlSignInSettings>, AwsSetupStatus>.Fail(AwsSetupStatus.Invalid,
                "The assertion address must be a full http or https address.", "acsUrl");
        if (AwsRules.NotAnAddress(settings.IdpSingleSignOnUrl))
            return Attempt<AwsSettingsSaved<SamlSignInSettings>, AwsSetupStatus>.Fail(AwsSetupStatus.Invalid,
                "The sign-in address must be a full http or https address.", "idpSingleSignOnUrl");

        // The latch first: sign-in configured with filtering off would show every user every AWS document.
        if (settings.IsConfigured && !configured.Current<PermissionEnforcementSettings>().IsEnforcing
            && !await AwsRules.LatchEnforcementAsync(store, reloader, configured, ct))
            return Attempt<AwsSettingsSaved<SamlSignInSettings>, AwsSetupStatus>.Fail(AwsSetupStatus.NotEnforcing,
                "Per-user enforcement for AWS couldn't be switched on in this process, so the sign-in application wasn't saved. "
                + "Restart Connapse and save again.");

        return Attempt<AwsSettingsSaved<SamlSignInSettings>, AwsSetupStatus>.Succeed(
            await AwsRules.SaveSamlAsync(store, reloader, audit, settings, ct));
    }
}

/// <summary>
/// Clears the recorded SAML application, for an administrator who removed it in AWS. Per-user enforcement stays on,
/// so searches of AWS sources return nothing for everyone until the application is set up again.
/// </summary>
[Operation("providers.aws.permissions.reset", Scope = "admin:settings", Risk = RiskTier.Destructive, Mcp = McpExposure.Generic)]
public sealed class ResetAwsPermissions(ISettingsStore store, ISettingsReloader reloader, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<AwsSettingsSaved<SamlSignInSettings>, AwsSetupStatus>> RunAsync(OperationContext context, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<AwsSettingsSaved<SamlSignInSettings>, AwsSetupStatus>.Fail(AwsSetupStatus.Forbidden, AwsRules.Refused);

        return Attempt<AwsSettingsSaved<SamlSignInSettings>, AwsSetupStatus>.Succeed(
            await AwsRules.SaveSamlAsync(store, reloader, audit, new SamlSignInSettings(), ct));
    }
}

/// <summary>Every AWS setup operation in one place, for the providers page.</summary>
public sealed class AwsProviderOperations(
    SetAwsAccess access, ResetAwsAccess resetAccess, SetIdentityCenter identityCenter, ResetIdentityCenter resetIdentityCenter,
    SetAwsPermissions permissions, ResetAwsPermissions resetPermissions)
{
    public SetAwsAccess Access { get; } = access;

    public ResetAwsAccess ResetAccess { get; } = resetAccess;

    public SetIdentityCenter IdentityCenter { get; } = identityCenter;

    public ResetIdentityCenter ResetIdentityCenter { get; } = resetIdentityCenter;

    public SetAwsPermissions Permissions { get; } = permissions;

    public ResetAwsPermissions ResetPermissions { get; } = resetPermissions;
}

/// <summary>The rules the AWS setup operations share.</summary>
internal static class AwsRules
{
    public const string Provider = "aws";

    public const string Refused = "You don't have permission to set up providers.";

    public const string AccessChanged =
        "The stored AWS credential changed since it was read, so nothing was changed. Read it again, review the values and try again.";

    public const string IdentityCenterCategory = "identitycenter";

    public const string SamlCategory = "samlsignin";

    /// <summary>The first value a credential is missing, with its field, or null.</summary>
    public static (string Name, string Field)? MissingAccessField(RolesAnywhereConfig config) =>
        config.CertificatePem.Length == 0 ? ("certificate", "certificatePem")
        : config.TrustAnchorArn.Length == 0 ? ("trust anchor ARN", "trustAnchorArn")
        : config.ProfileArn.Length == 0 ? ("profile ARN", "profileArn")
        : config.RoleArn.Length == 0 ? ("role ARN", "roleArn")
        : config.Region.Length == 0 ? ("region", "region")
        : null;

    /// <summary>A value that is set but isn't an absolute http or https address.</summary>
    public static bool NotAnAddress(string value) =>
        value.Length > 0
        && !(Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp));

    public static async Task<AwsSettingsSaved<IdentityCenterSettings>> SaveIdentityCenterAsync(
        ISettingsStore store, ISettingsReloader reloader, IAuditLogger audit, IdentityCenterSettings settings, CancellationToken ct)
    {
        await store.SaveAsync(IdentityCenterCategory, settings, ct);
        bool applied = Reload(reloader);
        // Blank values mean a reset.
        await audit.LogAsync("provider.identity_center.saved", "provider", Provider,
            new { settings.Region, settings.InstanceArn, settings.IdentityStoreId }, ct);
        return new(settings, applied);
    }

    public static async Task<AwsSettingsSaved<SamlSignInSettings>> SaveSamlAsync(
        ISettingsStore store, ISettingsReloader reloader, IAuditLogger audit, SamlSignInSettings settings, CancellationToken ct)
    {
        // Its own category, so recording enforcement never rewrites a SAML value configured in the environment.
        await store.SaveAsync(SamlCategory, settings, ct);
        bool applied = Reload(reloader);
        await audit.LogAsync("provider.saml.saved", "provider", Provider,
            new { settings.EntityId, settings.IdpEntityId, Configured = settings.IsConfigured }, ct);
        return new(settings, applied);
    }

    /// <summary>
    /// Switches the AWS enforcement latch on and reports whether this process now enforces. A merge under the
    /// store's lock, never a replace, so the Azure latch is carried along: a latch only ever goes on.
    /// </summary>
    public static async Task<bool> LatchEnforcementAsync(ISettingsStore store, ISettingsReloader reloader, IConfiguredSettings configured, CancellationToken ct)
    {
        PermissionEnforcementSettings current = configured.Current<PermissionEnforcementSettings>();
        await store.UpdateAsync<PermissionEnforcementSettings>(PermissionEnforcementSettings.Category, stored => new PermissionEnforcementSettings
        {
            IsEnforcing = true,
            AzureEnforcing = (stored?.AzureEnforcing ?? false) || current.AzureEnforcing,
        }, ct);
        Reload(reloader);
        return configured.Current<PermissionEnforcementSettings>().IsEnforcing;
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
