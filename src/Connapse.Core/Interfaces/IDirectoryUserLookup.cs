namespace Connapse.Core;

/// <summary>One user in the IAM Identity Center identity store, as Connapse needs to see them.</summary>
/// <param name="UserId">
/// The identity store's own identifier — the key access grants are held against, and what
/// <c>ListGroupMembershipsForMember</c> takes.
/// </param>
/// <param name="UserName">The name an administrator recognises in the console.</param>
/// <param name="Email">Display data. Nothing may authorize from it.</param>
/// <param name="Status">
/// Whether the directory reports this person as enabled, disabled, or does not say.
/// </param>
/// <remarks>
/// <see cref="Status"/> is why this type carries more than an id. Connapse holds no per-user
/// credential any more, so nothing expires on its own when somebody is deprovisioned — the only
/// thing that stops a disabled person searching is Connapse noticing, which means asking.
/// <para>
/// Three states rather than a bool, because the directory genuinely has three answers and the
/// third one used to be folded into "enabled". <c>UserStatus</c> is not populated for every
/// identity source, and reading an absent value as permission to continue meant a deprovisioned
/// person in one of those directories kept their grants indefinitely — the stored link has no
/// expiry, so nothing else would ever have caught it.
/// </para>
/// </remarks>
public record DirectoryUser(
    string UserId, string UserName, string? Email, DirectoryUserStatus Status)
{
    /// <summary>True only when the directory said so.</summary>
    /// <remarks>
    /// <see cref="DirectoryUserStatus.Unknown"/> is deliberately not enabled. It means the question
    /// was not answered, and an unanswered question is not a permit.
    /// </remarks>
    public bool Enabled => Status is DirectoryUserStatus.Enabled;
}

/// <summary>What the directory says about whether a person is still active.</summary>
public enum DirectoryUserStatus
{
    /// <summary>The directory did not say. Treated as a denial, not as a yes.</summary>
    /// <remarks>
    /// <c>DescribeUser</c> does not populate <c>UserStatus</c> for every identity source. A
    /// deployment on one of those cannot detect deprovisioning through this call at all, so the
    /// honest answer is to stop rather than to assume — and an administrator who sees searches
    /// denied has something to investigate, where the alternative shows nobody anything.
    /// </remarks>
    Unknown,

    /// <summary>The directory reports them enabled.</summary>
    Enabled,

    /// <summary>The directory reports them suspended.</summary>
    Disabled,
}

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
