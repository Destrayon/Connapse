namespace Connapse.Storage.Data.Entities;

/// <summary>
/// The credential Connapse itself acts as against one cloud provider.
/// </summary>
/// <remarks>
/// One row per provider, keyed by provider name. Connapse has a single identity per cloud — the
/// same one every connection uses — so this replaces the credentials the SDK would otherwise pick
/// up from the environment rather than sitting beside them.
/// <para>
/// Provider-level rather than per-connection because Connapse's identity is one thing. A connection
/// narrows from it with <c>RoleArn</c>; it does not bring its own. This differs from Airbyte and
/// Fivetran, where each configured source carries its own authentication — that shape follows from
/// connecting to many customers' systems, which a self-hosted single-organisation product does not.
/// </para>
/// <para>
/// The secret is DataProtection ciphertext under its own purpose, never the raw key. The key ring
/// is persisted to the appdata volume with SetApplicationName, so it survives container
/// replacement — without that this table would lose its contents on every redeploy.
/// </para>
/// </remarks>
public class ProviderCredentialEntity
{
    /// <summary>Provider key — "aws", "azure". One credential each.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// The public half, stored in the clear so it can be displayed.
    /// </summary>
    /// <remarks>
    /// An access key id identifies a credential without authenticating anything, and showing it is
    /// how an administrator confirms which one is in use before replacing it.
    /// </remarks>
    public string PublicId { get; set; } = string.Empty;

    /// <summary>DataProtection ciphertext, purpose "ProviderCredential.v1".</summary>
    public string SecretProtected { get; set; } = string.Empty;

    /// <summary>The IAM user or principal this belongs to, for display.</summary>
    public string? PrincipalName { get; set; }

    /// <summary>
    /// When this credential was stored.
    /// </summary>
    /// <remarks>
    /// Shown in the UI. Guidance on static keys is to rotate them on a schedule, and a key whose
    /// age is invisible is one nobody rotates.
    /// </remarks>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// The last time Connapse made a call AWS honoured with this credential, or null if never.
    /// </summary>
    /// <remarks>
    /// The difference between "not working yet" and "not working any more", which age alone cannot
    /// tell. A brand-new key is refused for a while because IAM is eventually consistent; a key that
    /// has already worked and then stops has been deleted or revoked, and no amount of waiting fixes
    /// it. Without this the page offers to keep waiting for a credential that no longer exists.
    /// <para>
    /// Reset to null whenever the credential is replaced: a new key has proved nothing, whatever its
    /// predecessor did.
    /// </para>
    /// </remarks>
    public DateTime? VerifiedAt { get; set; }

    public Guid? CreatedByUserId { get; set; }

    /// <summary>PEM of the Roles Anywhere end-entity certificate (public; stored in the clear). Null for the access-key shape.</summary>
    public string? CertificatePem { get; set; }

    /// <summary>DataProtection ciphertext of the Roles Anywhere private key, purpose "ProviderCredential.v1". Null for the access-key shape.</summary>
    public string? PrivateKeyProtected { get; set; }

    /// <summary>Roles Anywhere trust-anchor ARN. Its presence is the signal that this row is a Roles Anywhere config.</summary>
    public string? TrustAnchorArn { get; set; }

    /// <summary>Roles Anywhere profile ARN.</summary>
    public string? ProfileArn { get; set; }

    /// <summary>The role this configuration assumes.</summary>
    public string? RoleArn { get; set; }

    /// <summary>Region whose rolesanywhere endpoint is called.</summary>
    public string? Region { get; set; }
}
