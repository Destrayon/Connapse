namespace Connapse.Core;

/// <summary>
/// The customer's IAM Identity Center instance — the directory that per-user AWS permissions are
/// ultimately resolved against.
/// </summary>
/// <remarks>
/// Discovered rather than typed. An administrator can read all three values out of the console, but
/// the region is the one people get wrong: Identity Center exists in exactly one region per
/// organisation and nothing they already have encodes it. A wrong region does not fail loudly — it
/// looks like having no instance at all.
/// <para>
/// Holds no secret. Every value here is an identifier the account owner can see in their own
/// console, which is why this is a plain settings category rather than anything protected.
/// </para>
/// <para>
/// Separate from <see cref="SamlSignInSettings"/> because it is found first and independently: the
/// application does not exist yet when this is answered, and the setup script needs the region from
/// here to look in the right place.
/// </para>
/// </remarks>
public class IdentityCenterSettings
{
    public const string SectionName = "Identity:IdentityCenter";

    /// <summary>The single region the instance lives in.</summary>
    public string Region { get; set; } = string.Empty;

    /// <summary><c>arn:aws:sso:::instance/ssoins-…</c>.</summary>
    public string InstanceArn { get; set; } = string.Empty;

    /// <summary><c>d-…</c>, the directory a trusted token issuer resolves a user into.</summary>
    public string IdentityStoreId { get; set; } = string.Empty;

    /// <summary>
    /// A directory group to grant S3 access to, or empty when none has been chosen.
    /// </summary>
    /// <remarks>
    /// Held so a connection can print a grant command with nothing left to fill in. Group discovery
    /// happens in CloudShell, so without this the id is on an administrator's screen for a moment
    /// and then gone — which is why the first version of that command shipped with a placeholder
    /// where the grantee belonged, and got run with the placeholder still in it.
    /// <para>
    /// A convenience, not a constraint. Granting different teams different buckets is the point of
    /// groups, so this is the offered default rather than the only grantee a command may name.
    /// </para>
    /// <para>
    /// Deliberately outside <see cref="IsConfigured"/>. The instance is located long before any
    /// group exists, and requiring one would make a perfectly good instance read as unconfigured
    /// and stall every step that builds on it.
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

    /// <summary>True once the instance has been located.</summary>
    /// <remarks>
    /// The ARN alone would do — the other two are derivable from a scan that produced it — but all
    /// three are checked so that a half-saved record reads as unconfigured rather than as something
    /// later steps can build on.
    /// </remarks>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Region)
        && !string.IsNullOrWhiteSpace(InstanceArn)
        && !string.IsNullOrWhiteSpace(IdentityStoreId);
}
