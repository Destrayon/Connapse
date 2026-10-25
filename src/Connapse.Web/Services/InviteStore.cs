using Connapse.Application.Ports;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Web.Services;

/// <summary>The invite operations' invitations, through the identity store and <see cref="InviteService"/>.</summary>
public sealed class InviteStore(
    InviteService invites,
    IInviteAuthorizationPolicy policy,
    UserManager<ConnapseUser> users,
    IDbContextFactory<ConnapseIdentityDbContext> factory) : IInviteStore
{
    public async Task<InvitePage> ListPendingAsync(int skip, int? take, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var pending = db.UserInvitations.AsNoTracking().Where(i => i.AcceptedAt == null && i.ExpiresAt > DateTime.UtcNow);
        int total = await pending.CountAsync(ct);

        IQueryable<UserInvitation> query = pending.Include(i => i.CreatedByUser).OrderByDescending(i => i.CreatedAt).ThenBy(i => i.Id).Skip(skip);
        if (take is int pageSize)
            query = query.Take(pageSize);
        return new InvitePage((await query.ToListAsync(ct)).Select(Of).ToList(), total);
    }

    public Task<bool> MayInviteAsync(Guid inviterId, string role, CancellationToken ct = default) =>
        policy.CanInviteWithRoleAsync(inviterId, role);

    public async Task<InviteCreation> CreateAsync(string email, string role, Guid createdBy, CancellationToken ct = default)
    {
        if (await users.FindByEmailAsync(email) is not null)
            return new InviteCreation(InviteWrite.UserExists, null, null);
        try
        {
            var (token, invitation) = await invites.CreateInviteAsync(email, role, createdBy);
            return new InviteCreation(InviteWrite.Done, Of(invitation), token);
        }
        catch (InvalidOperationException)
        {
            // The service refuses an address with a pending invite, or one that gained an account since the check above.
            return new InviteCreation(await users.FindByEmailAsync(email) is not null ? InviteWrite.UserExists : InviteWrite.PendingExists, null, null);
        }
    }

    public Task<bool> RevokeAsync(Guid id, CancellationToken ct = default) => invites.RevokeInviteAsync(id);

    private static Invite Of(UserInvitation i) =>
        new(i.Id, i.Email, i.Role, i.CreatedByUser?.DisplayName ?? i.CreatedByUser?.Email, i.CreatedAt, i.ExpiresAt);
}
