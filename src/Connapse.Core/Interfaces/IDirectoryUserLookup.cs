namespace Connapse.Core;

/// <summary>One user in the IAM Identity Center identity store, as Connapse needs to see them.</summary>
/// <param name="UserId">
/// The identity store's own identifier — the key access grants are held against, and what
/// <c>ListGroupMembershipsForMember</c> takes.
/// </param>
/// <param name="UserName">The name an administrator recognises in the console.</param>
/// <param name="Email">Display data. Nothing may authorize from it.</param>
/// <param name="Enabled">
/// False when the directory has suspended them.
/// </param>
/// <remarks>
/// <see cref="Enabled"/> is why this type carries more than an id. Connapse holds no per-user
/// credential any more, so nothing expires on its own when somebody is deprovisioned — the only
/// thing that stops a disabled person searching is Connapse noticing, which means asking.
/// </remarks>
public record DirectoryUser(string UserId, string UserName, string? Email, bool Enabled);

/// <summary>
/// Reads the IAM Identity Center directory using Connapse's own AWS identity.
/// </summary>
/// <remarks>
/// Deliberately read-only, and deliberately Connapse's identity rather than the user's. This is the
/// piece that replaced storing a per-user refresh token: rather than acting as somebody to discover
/// what they may read, Connapse asks the directory about them.
/// </remarks>
public interface IDirectoryUserLookup
{
    /// <summary>
    /// The identity store id for <paramref name="userName"/>, or null when the directory has no
    /// such user.
    /// </summary>
    /// <remarks>
    /// Called once when a person connects, not on every search. The id is stored because it is the
    /// stable identifier: renaming somebody changes the name and not this.
    /// </remarks>
    Task<string?> FindUserIdAsync(string userName, CancellationToken ct = default);

    /// <summary>
    /// The user behind <paramref name="userId"/>, or null when the directory no longer has them.
    /// </summary>
    /// <remarks>
    /// Null and <see cref="DirectoryUser.Enabled"/> being false are different facts with the same
    /// consequence — deleted and suspended — and both must deny. They are kept apart so that what
    /// is reported to an administrator says which happened.
    /// </remarks>
    Task<DirectoryUser?> DescribeAsync(string userId, CancellationToken ct = default);

    /// <summary>The identity store ids of the groups <paramref name="userId"/> belongs to.</summary>
    /// <remarks>
    /// Needed because <c>ListAccessGrants</c> does not expand membership: a grantee filter matches
    /// the grant record literally, so a grant made to a group is invisible when asking about one of
    /// its members. This is the work AWS does inside <c>ListCallerAccessGrants</c> and that
    /// Connapse takes on in exchange for not holding anybody's credential.
    /// </remarks>
    Task<IReadOnlyList<string>> ListGroupIdsAsync(string userId, CancellationToken ct = default);
}
