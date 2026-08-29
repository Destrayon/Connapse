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
    /// The IAM Identity Center user name the token names, and the join key into the directory.
    /// </summary>
    /// <remarks>
    /// AWS accepts only user name, email or external ID as the claim mapped to a directory user, so
    /// the opaque OIDC subject cannot serve here. Of those three this is the one that always works:
    /// external ID is populated only by SCIM sync, and a federated user's email is unverified —
    /// Cognito marks it so by default and cannot verify it with a one-time code.
    /// <para>
    /// Stored rather than re-read from the token so the integrations page can say which identity is
    /// connected without decrypting anything. Stored with its case intact: this identifier belongs
    /// to a directory Connapse does not own.
    /// </para>
    /// <para>
    /// Empty on a row written before the join key moved off email. Such a row cannot resolve and
    /// the user has to connect again; it is left in place rather than deleted so that nothing
    /// silently discards a link Connapse did not author.
    /// </para>
    /// </remarks>
    public string DirectoryUserName { get; set; } = string.Empty;

    /// <summary>
    /// The email the token carried, for display. Empty when it carried none.
    /// </summary>
    /// <remarks>
    /// Display data only, and unverified for any federated user. Nothing may make an authorization
    /// decision from it — that is what <see cref="DirectoryUserName"/> is for.
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
