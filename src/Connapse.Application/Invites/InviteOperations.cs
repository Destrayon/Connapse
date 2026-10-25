using System.Net.Mail;
using Connapse.Application.Ports;
using Connapse.Application.Users;
using Connapse.Core.Interfaces;

namespace Connapse.Application.Invites;

/// <summary>Why an invite operation didn't succeed.</summary>
public enum InviteStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(400, "invalid-page", "Invalid page")]
    InvalidPage,

    [Failure(400, "invalid-invite", "Invalid invite")]
    InvalidInvite,

    [Failure(409, "user-exists", "The user already exists")]
    UserExists,

    [Failure(409, "invite-pending", "An invite is already pending")]
    InvitePending,

    [Failure(404, "invite-not-found", "Invite not found")]
    NotFound,
}

/// <summary>One page of pending invites. <see cref="Take"/> null means every one, which only the User management page asks for.</summary>
public sealed record ListInvitesQuery(int Skip = 0, int? Take = null);

/// <summary>Pending invites newest first, how many there are, and whether another page follows.</summary>
public sealed record InviteList(IReadOnlyList<Invite> Items, int TotalCount, bool HasMore);

/// <summary>Who to invite, and the role their account starts with.</summary>
public sealed record CreateInviteRequest(string? Email, string? Role);

/// <summary>A new invite and the token its link carries: answered this once, to the administrator who made it.</summary>
public sealed record CreatedInvite(Invite Invite, string Token);

internal static class InviteRules
{
    public const int MaxEmailLength = 256;

    public const string NoPermission = "You don't have permission to manage users.";

    /// <summary>Invites are user management: an administrator, holding the admin:users scope.</summary>
    public static async Task<bool> MayManageAsync(IOperationAuthorizer authorizer, OperationContext context) =>
        await authorizer.AllowsAsync(context.User, Policies.Admin) && await authorizer.AllowsAsync(context.User, Policies.AdminUsersScope);
}

/// <summary>Lists pending invites, administrators only. An invite's token is never listed.</summary>
[Operation("invites.list", Scope = "admin:users", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class ListInvites(IInviteStore store, IOperationAuthorizer authorizer)
{
    public const int MaxTake = 200;

    public async Task<Attempt<InviteList, InviteStatus>> RunAsync(OperationContext context, ListInvitesQuery query, CancellationToken ct = default)
    {
        if (!await InviteRules.MayManageAsync(authorizer, context))
            return Attempt<InviteList, InviteStatus>.Fail(InviteStatus.Forbidden, InviteRules.NoPermission);
        if (query.Skip < 0)
            return Attempt<InviteList, InviteStatus>.Fail(InviteStatus.InvalidPage, "skip must be >= 0");
        if (query.Take is < 1)
            return Attempt<InviteList, InviteStatus>.Fail(InviteStatus.InvalidPage, "take must be >= 1");
        if (query.Take is > MaxTake)
            return Attempt<InviteList, InviteStatus>.Fail(InviteStatus.InvalidPage, $"take must be <= {MaxTake}");

        var page = await store.ListPendingAsync(query.Skip, query.Take, ct);
        bool hasMore = query.Take is int take && query.Skip + take < page.TotalCount;
        return Attempt<InviteList, InviteStatus>.Succeed(new InviteList(page.Invites, page.TotalCount, hasMore));
    }
}

/// <summary>
/// Invites someone, administrators only: a link that makes their account, with the role given here.
/// Owner and Agent are never given, and Admin only where the instance's invite policy allows it
/// (by default, by the Owner). The token is answered once, in the result, and never stored or logged.
/// </summary>
[Operation("invites.create", Scope = "admin:users", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class CreateInvite(IInviteStore store, IOperationAuthorizer authorizer, IAuditLogger audit)
{
    public const string CreatedEvent = "invite.created";

    public async Task<Attempt<CreatedInvite, InviteStatus>> RunAsync(OperationContext context, CreateInviteRequest request, CancellationToken ct = default)
    {
        if (!await InviteRules.MayManageAsync(authorizer, context) || UserRules.CallerId(context.User) is not Guid callerId)
            return Attempt<CreatedInvite, InviteStatus>.Fail(InviteStatus.Forbidden, InviteRules.NoPermission);

        string email = request.Email?.Trim() ?? "";
        if (email.Length == 0)
            return Attempt<CreatedInvite, InviteStatus>.Fail(InviteStatus.InvalidInvite, "Email is required.", "email");
        if (email.Length > InviteRules.MaxEmailLength)
            return Attempt<CreatedInvite, InviteStatus>.Fail(InviteStatus.InvalidInvite, $"Email must be at most {InviteRules.MaxEmailLength} characters.", "email");
        if (!MailAddress.TryCreate(email, out var address) || address.Address != email)
            return Attempt<CreatedInvite, InviteStatus>.Fail(InviteStatus.InvalidInvite, $"'{email}' is an invalid email address.", "email");

        string role = request.Role?.Trim() ?? "";
        if (role.Equals("Owner", StringComparison.OrdinalIgnoreCase) || role.Equals("Agent", StringComparison.OrdinalIgnoreCase))
            return Attempt<CreatedInvite, InviteStatus>.Fail(InviteStatus.InvalidInvite, $"The '{role}' role is not allowed in an invitation.", "role");
        if (UserRules.Assignable.FirstOrDefault(a => a.Equals(role, StringComparison.OrdinalIgnoreCase)) is not { } assignable)
            return Attempt<CreatedInvite, InviteStatus>.Fail(InviteStatus.InvalidInvite,
                $"'{role}' is an invalid role. Roles: {string.Join(", ", UserRules.Assignable)}.", "role");
        if (!await store.MayInviteAsync(callerId, assignable, ct))
            return Attempt<CreatedInvite, InviteStatus>.Fail(InviteStatus.Forbidden, "Only the Owner can invite users with the Admin role.");

        var created = await store.CreateAsync(email, assignable, callerId, ct);
        if (created.Outcome == InviteWrite.UserExists)
            return Attempt<CreatedInvite, InviteStatus>.Fail(InviteStatus.UserExists, $"A user with email '{email}' already exists.", "email");
        if (created.Outcome == InviteWrite.PendingExists)
            return Attempt<CreatedInvite, InviteStatus>.Fail(InviteStatus.InvitePending, $"A pending invitation for '{email}' already exists.", "email");

        var invite = created.Invite!;
        // The role, never the token: an audit row is read by every administrator.
        await audit.LogAsync(CreatedEvent, "invite", invite.Id.ToString(), new { invite.Role }, ct);
        return Attempt<CreatedInvite, InviteStatus>.Succeed(new CreatedInvite(invite, created.Token!));
    }
}

/// <summary>Revokes a pending invite, administrators only, so its link stops working.</summary>
[Operation("invites.revoke", Scope = "admin:users", Risk = RiskTier.Destructive, Mcp = McpExposure.Generic)]
public sealed class RevokeInvite(IInviteStore store, IOperationAuthorizer authorizer, IAuditLogger audit)
{
    public const string RevokedEvent = "invite.revoked";

    public async Task<Attempt<Guid, InviteStatus>> RunAsync(OperationContext context, Guid inviteId, CancellationToken ct = default)
    {
        if (!await InviteRules.MayManageAsync(authorizer, context))
            return Attempt<Guid, InviteStatus>.Fail(InviteStatus.Forbidden, InviteRules.NoPermission);
        if (!await store.RevokeAsync(inviteId, ct))
            return Attempt<Guid, InviteStatus>.Fail(InviteStatus.NotFound, "Invite not found");

        await audit.LogAsync(RevokedEvent, "invite", inviteId.ToString(), null, ct);
        return Attempt<Guid, InviteStatus>.Succeed(inviteId);
    }
}

/// <summary>Every invite operation in one place, for the User management page's invite card.</summary>
public sealed class InviteOperations(ListInvites list, CreateInvite create, RevokeInvite revoke)
{
    public ListInvites List { get; } = list;

    public CreateInvite Create { get; } = create;

    public RevokeInvite Revoke { get; } = revoke;
}
