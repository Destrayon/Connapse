namespace Connapse.Identity.Data.Entities;

/// <summary>
/// A Connapse user's connected AWS identity — which IAM Identity Center user they signed in as.
/// </summary>
/// <remarks>
/// Holds no credential. IAM Identity Center attests the identity once, at sign-in, and Connapse
/// records who it named; permissions are then read with Connapse's own IAM identity rather than
/// with anything belonging to the person. There is nothing here to expire, and nothing here worth
/// stealing.
/// <para>
/// Separate from the now-removed generic cloud-identity scaffolding on purpose. That table recorded
/// which cloud account a user signed into, as plaintext metadata in a JSON column, and it predates
/// this feature.
/// </para>
/// <para>
/// One row per user, enforced by a unique index. Connecting again replaces the row rather than
/// adding a second, so there is never a question of which identity is current.
/// </para>
/// </remarks>
public class UserAwsIdentityLinkEntity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>
    /// The identity store's own identifier for the user — the key access grants are held against.
    /// </summary>
    /// <remarks>
    /// Resolved once at sign-in by <c>identitystore:GetUserId</c> from the asserted user name, and
    /// stored rather than re-resolved because it is the stable identifier: renaming somebody in the
    /// directory changes their user name and not this, so a rename does not force them to connect
    /// again. It is also what <c>ListGroupMembershipsForMember</c> takes, so it is needed twice per
    /// resolve.
    /// <para>
    /// Empty on a row written before this column existed. Such a row cannot resolve and the user
    /// has to connect again; it is left in place rather than deleted so nothing silently discards a
    /// link Connapse did not author.
    /// </para>
    /// </remarks>
    public string DirectoryUserId { get; set; } = string.Empty;

    /// <summary>
    /// The IAM Identity Center user name the assertion named, for display and for reconnecting.
    /// </summary>
    /// <remarks>
    /// Stored with its case intact: this identifier belongs to a directory Connapse does not own,
    /// and lower-casing it would record something that may never have existed. Not the join key —
    /// <see cref="DirectoryUserId"/> is — but it is what an administrator recognises in the
    /// console, so it is what the integrations page shows.
    /// </remarks>
    public string DirectoryUserName { get; set; } = string.Empty;

    /// <summary>
    /// The email the assertion carried, for display. Empty when it carried none.
    /// </summary>
    /// <remarks>
    /// Display data only. Nothing may make an authorization decision from it — that is what
    /// <see cref="DirectoryUserId"/> is for.
    /// </remarks>
    public string Email { get; set; } = string.Empty;

    public DateTime ConnectedAt { get; set; }

    public DateTime? LastUsedAt { get; set; }

    public ConnapseUser User { get; set; } = null!;
}
