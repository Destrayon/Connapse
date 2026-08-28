namespace Connapse.Identity.Data.Entities;

/// <summary>
/// A Connapse user's connected AWS identity, and the token that lets Connapse prove it again later
/// without them present.
/// </summary>
/// <remarks>
/// Separate from <see cref="UserCloudIdentityEntity"/> on purpose. That entity records which cloud
/// account a user signed into, as plaintext metadata in a JSON column, and it predates this feature.
/// A refresh token is a per-user secret; putting one in a column built for display metadata would
/// be a mistake nothing in the type system would catch.
/// <para>
/// One row per user, enforced by a unique index. Connecting again replaces the row rather than
/// adding a second, so there is never a question of which token is current.
/// </para>
/// </remarks>
public class UserAwsIdentityLinkEntity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>
    /// The verified email the token was issued for, and the join key into IAM Identity Center.
    /// </summary>
    /// <remarks>
    /// Stored rather than re-read from the token because it is what a later exchange is matched on,
    /// and because it lets the integrations page say which identity is connected without decrypting
    /// anything. AWS accepts only user name, email or external ID as the claim mapped to a directory
    /// user, so the opaque OIDC subject cannot serve here.
    /// </remarks>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// The Cognito refresh token, already through ASP.NET Core Data Protection.
    /// </summary>
    /// <remarks>
    /// Named for its state so that assigning a plaintext token reads as wrong at the call site.
    /// Only <c>AwsIdentityLinkStore</c> protects and unprotects it; nothing else should hold the
    /// plaintext for longer than one exchange.
    /// </remarks>
    public string ProtectedRefreshToken { get; set; } = string.Empty;

    public DateTime ConnectedAt { get; set; }

    public DateTime? LastUsedAt { get; set; }

    public ConnapseUser User { get; set; } = null!;
}
