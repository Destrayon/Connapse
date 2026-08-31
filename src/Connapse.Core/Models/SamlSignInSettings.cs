namespace Connapse.Core;

/// <summary>
/// The IAM Identity Center SAML application a person signs in through to prove which directory
/// user they are.
/// </summary>
/// <remarks>
/// Two halves that travel in opposite directions. <see cref="EntityId"/> and <see cref="AcsUrl"/>
/// are Connapse's, and an administrator types them into the AWS console when creating the
/// application. The three <c>Idp</c> values are Identity Center's, and come back the other way out
/// of that application's metadata.
/// <para>
/// The identity provider's metadata is stored decomposed rather than as a URL Connapse fetches at
/// sign-in. A self-hosted deployment need not have outbound access to AWS — the assertion reaches
/// it through the person's browser, not over the network — and a metadata fetch would put an
/// outbound dependency in the one path that has none. It would also turn an AWS outage into a
/// sign-in outage.
/// </para>
/// <para>
/// Holds no secret. A signing certificate is a public key, and the two Connapse values are printed
/// on the setup page for an administrator to copy. Nothing here is withheld from the settings API.
/// </para>
/// </remarks>
public class SamlSignInSettings
{
    public const string SectionName = "Identity:SamlSignIn";

    /// <summary>
    /// Connapse's SAML entity id, which the application registers as its audience.
    /// </summary>
    /// <remarks>
    /// Checked against every assertion's <c>AudienceRestriction</c>. An assertion minted for some
    /// other service provider is refused here, which is what stops one issued for a different
    /// application in the same directory from being replayed at Connapse.
    /// </remarks>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>Where Identity Center POSTs the assertion — Connapse's assertion consumer service.</summary>
    /// <remarks>
    /// Also checked against the assertion's <c>Destination</c>. It has to match what was registered
    /// in the console exactly, so moving a deployment to a new URL means editing the application.
    /// </remarks>
    public string AcsUrl { get; set; } = string.Empty;

    /// <summary>Identity Center's entity id, the <c>Issuer</c> every assertion must carry.</summary>
    public string IdpEntityId { get; set; } = string.Empty;

    /// <summary>Where the sign-in request is sent, from the application's metadata.</summary>
    public string IdpSingleSignOnUrl { get; set; } = string.Empty;

    /// <summary>
    /// The base64 X.509 certificate assertions are signed with, from the same metadata.
    /// </summary>
    /// <remarks>
    /// The whole of the trust. Identity Center rotates it, and a rotation that is not reflected
    /// here fails every sign-in until the metadata is pasted again.
    /// </remarks>
    public string IdpSigningCertificate { get; set; } = string.Empty;

    /// <summary>True once both halves have been exchanged and sign-in can be attempted.</summary>
    /// <remarks>
    /// All five, because a partial record is worse than an empty one: without the certificate
    /// there is nothing to validate against, and a sign-in that got that far would have to either
    /// fail late or trust an unverified assertion.
    /// </remarks>
    /// <summary>
    /// A directory group to grant S3 access to, or empty when none has been chosen.
    /// </summary>
    /// <remarks>
    /// Here rather than with the Identity Center instance, which is only "where is the directory".
    /// A group is not a directory fact Connapse happens to hold — it exists solely to be the
    /// grantee on an access grant, which is what per-user permissions are made of. Its setup step
    /// is in this feature and connections read it to print a grant command, so it belongs to the
    /// thing being configured rather than to AWS's own taxonomy.
    /// <para>
    /// Held so a connection can print a grant command with nothing left to fill in. Group discovery
    /// happens in CloudShell, so without this the id is on an administrator's screen for a moment
    /// and then gone — which is why the first version of that command shipped with a placeholder
    /// where the grantee belonged, and got run with the placeholder still in it.
    /// </para>
    /// <para>
    /// A convenience, not a constraint. Granting different teams different buckets is the point of
    /// groups, so this is the offered default rather than the only grantee a command may name.
    /// </para>
    /// <para>
    /// Deliberately outside <see cref="IsConfigured"/>. Sign-in works perfectly without a group;
    /// requiring one would make a working sign-in read as unconfigured and stall the steps that
    /// depend on it.
    /// </para>
    /// </remarks>
    public string GrantGroupId { get; set; } = string.Empty;

    /// <summary>The group's display name, for showing which group a command names.</summary>
    /// <remarks>
    /// Stored alongside the id because the id is a UUID nobody recognises, and reading it back from
    /// AWS would need a directory call on every page that mentions it.
    /// </remarks>
    public string GrantGroupName { get; set; } = string.Empty;

    /// <summary>Whether a group has been chosen to grant to.</summary>
    public bool HasGrantGroup => !string.IsNullOrWhiteSpace(GrantGroupId);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(EntityId)
        && !string.IsNullOrWhiteSpace(AcsUrl)
        && !string.IsNullOrWhiteSpace(IdpEntityId)
        && !string.IsNullOrWhiteSpace(IdpSingleSignOnUrl)
        && !string.IsNullOrWhiteSpace(IdpSigningCertificate);

    /// <summary>
    /// Whether this deployment has ever had per-user permissions working, and so must keep
    /// enforcing them.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="IsConfigured"/> on purpose, and the distinction is load-bearing.
    /// Search filtering used to be switched on by configuration being complete, which meant the
    /// same fields decided both "is sign-in set up" and "are permissions enforced" — so blanking
    /// one of them turned a filtered deployment into an unfiltered one. Clearing the signing
    /// certificate to paste a rotated one does exactly that, and it is an ordinary maintenance
    /// step. The failure was inverted: sign-in broke <i>and</i> filtering stopped, at the same
    /// moment, with nothing said.
    /// <para>
    /// So this latches. It is set the first time a complete configuration is saved and is cleared
    /// only by an administrator deciding to stop enforcing. Once set, incomplete configuration
    /// denies rather than opens: an installation that has never configured SAML is unrestricted,
    /// and one that has is not, whatever state its settings are in.
    /// </para>
    /// </remarks>
    public bool EnforcementEnabled { get; set; }

    /// <summary>
    /// Whether a search must be filtered, and whether the settings can currently answer that.
    /// </summary>
    /// <remarks>
    /// Three outcomes rather than a bool, because "do not filter" and "cannot say" are opposites
    /// and a bool would have to pick one of them to represent both.
    /// </remarks>
    public EnforcementState Enforcement =>
        !EnforcementEnabled ? EnforcementState.NotEnforcing
        : IsConfigured ? EnforcementState.Enforcing
        : EnforcementState.EnforcingButUnusable;
}

/// <summary>Whether per-user permissions are being enforced, and whether they can be.</summary>
public enum EnforcementState
{
    /// <summary>Never switched on. Searches are unfiltered, which is the default.</summary>
    NotEnforcing,

    /// <summary>Switched on and configured. Searches resolve against the directory.</summary>
    Enforcing,

    /// <summary>
    /// Switched on, but the configuration cannot answer. Searches deny.
    /// </summary>
    /// <remarks>
    /// A blanked field, a failed settings load, a half-finished edit. The correct response is to
    /// stop answering rather than to stop filtering — somebody investigating an empty result page
    /// finds a broken setting, where the alternative leaves nobody investigating anything.
    /// </remarks>
    EnforcingButUnusable,
}
