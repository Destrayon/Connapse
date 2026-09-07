namespace Connapse.Core;

/// <summary>Whether per-user permissions are being enforced, and whether they can be.</summary>
public enum EnforcementState
{
    /// <summary>Never switched on. Searches are unfiltered, which is the default.</summary>
    NotEnforcing,

    /// <summary>Switched on and configured. Searches resolve against the directory.</summary>
    Enforcing,

    /// <summary>
    /// Switched on, but permissions cannot currently be determined. Searches deny.
    /// </summary>
    /// <remarks>
    /// A blanked field, a failed settings load, a startup migration that could not complete. The
    /// correct response is to stop answering rather than to stop filtering — somebody investigating
    /// an empty result page finds a broken setting, where the alternative leaves nobody
    /// investigating anything.
    /// </remarks>
    EnforcingButUnusable,
}

/// <summary>
/// Whether this deployment filters search results by what each person may read.
/// </summary>
/// <remarks>
/// <b>Its own settings category, deliberately.</b> This began as a flag on
/// <see cref="SamlSignInSettings"/>, which meant the only way to record it was to write that whole
/// object back to the database — and the database outranks appsettings and environment variables,
/// so a deployment configuring SAML through environment would have had those values copied into a
/// row that then shadowed them permanently. Editing the environment would appear to do nothing.
/// <para>
/// Separating the marker removes the choice between recording enforcement and leaving external
/// configuration alone. Nothing here is a SAML value, so writing it disturbs nothing.
/// </para>
/// <para>
/// It also keeps the two questions apart, which is the point of the flag existing at all.
/// "Is sign-in set up" and "are permissions enforced" used to be the same test, so blanking a SAML
/// field — clearing the signing certificate to paste a rotated one, say — switched filtering off
/// while breaking sign-in at the same moment, when nobody was likely to be watching search results.
/// </para>
/// </remarks>
public class PermissionEnforcementSettings
{
    public const string SectionName = "Identity:PermissionEnforcement";

    /// <summary>The settings category this is stored under.</summary>
    public const string Category = "permissionenforcement";

    /// <summary>
    /// True once this deployment has had SAML per-user permissions working.
    /// </summary>
    /// <remarks>
    /// Latches. It is set when a complete SAML sign-in configuration is first saved, or by the
    /// startup migration for a deployment that was already filtering before this flag existed, and is
    /// cleared only by an administrator deciding to stop enforcing. Once set, an incomplete
    /// configuration denies rather than opens.
    /// <para>
    /// This marker is <b>per identity provider</b>: it governs SAML/AWS only, and Azure has its own
    /// <see cref="AzureEnforcing"/>. They must stay independent — a single shared bit would make
    /// configuring one provider silently switch the OTHER's corpus from unfiltered to denied (once
    /// latched, an unconfigured provider's resolver returns
    /// <see cref="EnforcementState.EnforcingButUnusable"/>), which both changes that provider's
    /// behaviour and hides documents that were meant to be visible.
    /// </para>
    /// </remarks>
    public bool IsEnforcing { get; set; }

    /// <summary>True once this deployment has had Azure AD per-user permissions working.</summary>
    /// <remarks>
    /// The Azure counterpart to <see cref="IsEnforcing"/>, latched independently so that configuring
    /// Azure AD never disturbs the SAML/AWS enforcement state, and vice versa. See the remarks on
    /// <see cref="IsEnforcing"/> for why the two markers must not be shared.
    /// </remarks>
    public bool AzureEnforcing { get; set; }

    /// <summary>What a resolver should do, given its own latch and whether its provider is configured.</summary>
    /// <param name="latched">True once this provider has had per-user permissions working.</param>
    /// <param name="providerConfigured">True when the provider has a complete sign-in configuration.</param>
    /// <param name="determined">
    /// False when the startup migration could not establish whether this deployment was already
    /// enforcing. An undetermined deployment enforces: not knowing is not permission to open.
    /// </param>
    private static EnforcementState StateFrom(bool latched, bool providerConfigured, bool determined)
    {
        if (!determined)
            return EnforcementState.EnforcingButUnusable;

        if (!latched)
            return EnforcementState.NotEnforcing;

        return providerConfigured
            ? EnforcementState.Enforcing
            : EnforcementState.EnforcingButUnusable;
    }

    /// <summary>What the Azure resolver should do, given whether Azure AD sign-in is configured.
    /// Reads the independent <see cref="AzureEnforcing"/> latch.</summary>
    public EnforcementState StateForAzure(bool azureConfigured, bool determined = true) =>
        StateFrom(AzureEnforcing, azureConfigured, determined);

    /// <summary>What the AWS resolver should do, given the SAML sign-in settings it has. Reads the
    /// SAML latch (<see cref="IsEnforcing"/>) — behaviour is unchanged from before Azure existed.</summary>
    public EnforcementState StateFor(SamlSignInSettings signIn, bool determined = true)
    {
        ArgumentNullException.ThrowIfNull(signIn);
        return StateFrom(IsEnforcing, signIn.IsConfigured, determined);
    }
}

/// <summary>
/// Whether the startup migration managed to work out this deployment's enforcement state.
/// </summary>
/// <remarks>
/// Singleton, set once at startup and read on every resolution. It exists because the migration has
/// a third outcome besides "was enforcing" and "was not": it may have been unable to find out, if
/// reading or writing settings threw.
/// <para>
/// The first version of that migration logged the failure and carried on with enforcement off,
/// under the reasoning that startup should never be blocked. That reasoning was right and the
/// chosen failure was wrong — on a deployment that had been filtering, one transient database error
/// at boot opened the entire corpus, and the only trace was a log line. Continuing to run while
/// refusing to answer is the version of "do not block startup" that does not do that.
/// </para>
/// </remarks>
public sealed class EnforcementMigration
{
    private volatile bool determined;

    /// <summary>True once the migration has established the state, one way or the other.</summary>
    /// <remarks>
    /// Volatile because it is written once on the startup thread and read afterwards on every
    /// request thread. Without it a reader could keep observing the initial false and refuse
    /// searches on a deployment whose migration succeeded.
    /// </remarks>
    public bool Determined => determined;

    /// <summary>Records that the state is known.</summary>
    public void Complete() => determined = true;

    /// <summary>A migration that never ran, for tests and for deployments without one.</summary>
    public static EnforcementMigration Completed()
    {
        var migration = new EnforcementMigration();
        migration.Complete();
        return migration;
    }
}
