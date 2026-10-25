namespace Connapse.Application.Ports;

/// <summary>A pending invitation as the invite operations see it. Its token is never part of it.</summary>
public sealed record Invite(Guid Id, string Email, string Role, string? InvitedBy, DateTime CreatedAt, DateTime ExpiresAt);

/// <summary>One page of pending invitations, newest first, and how many there are in all.</summary>
public sealed record InvitePage(IReadOnlyList<Invite> Invites, int TotalCount);

/// <summary>How making an invitation went.</summary>
public enum InviteWrite
{
    Done,

    /// <summary>Someone already has an account with that email; nothing was made.</summary>
    UserExists,

    /// <summary>That email already has a pending invitation; nothing was made.</summary>
    PendingExists,
}

/// <summary>
/// A new invitation and its token, the secret its link carries. The store keeps only the token's hash,
/// so this is the one time it can be answered.
/// </summary>
public sealed record InviteCreation(InviteWrite Outcome, Invite? Invite, string? Token);

/// <summary>
/// The invitations the invite operations manage. Web answers it with the identity store; the
/// operations own every rule about who may invite whom.
/// </summary>
public interface IInviteStore
{
    /// <summary>Pending invitations newest first; <paramref name="take"/> null means every one from <paramref name="skip"/> on.</summary>
    Task<InvitePage> ListPendingAsync(int skip, int? take, CancellationToken ct = default);

    /// <summary>Whether <paramref name="inviterId"/> may invite someone with <paramref name="role"/>, by the instance's invite policy.</summary>
    Task<bool> MayInviteAsync(Guid inviterId, string role, CancellationToken ct = default);

    Task<InviteCreation> CreateAsync(string email, string role, Guid createdBy, CancellationToken ct = default);

    /// <summary>Deletes a pending invitation; false when there's no such invitation or it was already accepted.</summary>
    Task<bool> RevokeAsync(Guid id, CancellationToken ct = default);
}
