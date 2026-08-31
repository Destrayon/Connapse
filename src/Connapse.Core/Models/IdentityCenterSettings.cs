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
