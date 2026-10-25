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
    ConnapseIdentityDbContext db,
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
        // One create per address at a time, so two can't both pass the pending check and each make a link.
        // The service, the user manager and this context share the request's DbContext, so its checks and
        // insert run inside this transaction, under the lock.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext(lower({email})))", ct);

        if (await users.FindByEmailAsync(email) is not null)
            return new InviteCreation(InviteWrite.UserExists, null, null);
        if (await db.UserInvitations.AnyAsync(i => i.Email == email && i.AcceptedAt == null && i.ExpiresAt > DateTime.UtcNow, ct))
            return new InviteCreation(InviteWrite.PendingExists, null, null);
        try
        {
            var (token, invitation) = await invites.CreateInviteAsync(email, role, createdBy);
            await transaction.CommitAsync(ct);
            return new InviteCreation(InviteWrite.Done, Of(invitation), token);
        }
        catch (InvalidOperationException)
        {
            // Under the lock the account and pending checks above still hold, so what's left to refuse is the
            // policy, asked again at the write.
            return new InviteCreation(InviteWrite.Forbidden, null, null);
        }
    }

    public Task<bool> RevokeAsync(Guid id, CancellationToken ct = default) => invites.RevokeInviteAsync(id);

    private static Invite Of(UserInvitation i) =>
        new(i.Id, i.Email, i.Role, i.CreatedByUser?.DisplayName ?? i.CreatedByUser?.Email, i.CreatedAt, i.ExpiresAt);
}
