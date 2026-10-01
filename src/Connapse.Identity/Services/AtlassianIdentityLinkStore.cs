using Connapse.Core.Interfaces;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Connapse.Identity.Services;

/// <summary>Reads and writes which Atlassian account a Connapse user signed in as.</summary>
/// <remarks>Holds no token, mirroring <see cref="GitHubIdentityLinkStore"/>.</remarks>
public sealed class AtlassianIdentityLinkStore(
    IDbContextFactory<ConnapseIdentityDbContext> factory,
    TimeProvider timeProvider) : IAtlassianIdentityLinkReader
{
    public async Task<AtlassianIdentityRef?> GetLinkAsync(Guid userId, CancellationToken ct = default)
    {
        var link = await GetAsync(userId, ct);
        return link is null ? null : new AtlassianIdentityRef(link.AccountId, link.DisplayName);
    }

    public async Task<UserAtlassianIdentityLinkEntity?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.UserAtlassianIdentityLinks.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct);
    }

    /// <summary>Stores a user's link, replacing any existing one.</summary>
    /// <remarks>Two concurrent connects race on the unique index; the loser updates the winner's row,
    /// as <see cref="GitHubIdentityLinkStore.SaveAsync"/> does.</remarks>
    public async Task SaveAsync(
        Guid userId, string accountId, string displayName, string? email, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.UserAtlassianIdentityLinks.SingleOrDefaultAsync(x => x.UserId == userId, ct);

        if (existing is null)
        {
            var candidate = new UserAtlassianIdentityLinkEntity
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                AccountId = accountId,
                DisplayName = displayName,
                Email = email,
                ConnectedAt = timeProvider.GetUtcNow().UtcDateTime,
            };
            db.UserAtlassianIdentityLinks.Add(candidate);

            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.Entry(candidate).State = EntityState.Detached;
                existing = await db.UserAtlassianIdentityLinks.SingleAsync(x => x.UserId == userId, ct);
            }
        }

        existing.AccountId = accountId;
        existing.DisplayName = displayName;
        existing.Email = email;
        existing.ConnectedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.UserAtlassianIdentityLinks.SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (existing is null)
            return false;

        db.UserAtlassianIdentityLinks.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
