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

    /// <summary>How many times the user has unlinked. A sign-in records this when it starts and passes
    /// it to <see cref="TrySaveAsync"/>.</summary>
    public async Task<long> GetRevocationGenerationAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.UserAtlassianLinkRevocations.AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => x.Generation)
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>Stores a user's link, replacing any existing one.</summary>
    public async Task SaveAsync(
        Guid userId, string accountId, string displayName, string? email, CancellationToken ct = default) =>
        await SaveCoreAsync(userId, expectedGeneration: null, accountId, displayName, email, ct);

    /// <summary>
    /// Stores a user's link only if they have not unlinked since <paramref name="revocationGeneration"/>
    /// was read. The check and the write share one transaction holding the user's revocation row, so an
    /// unlink either commits first and this saves nothing, or waits and deletes what this saved.
    /// </summary>
    /// <returns>False, with nothing written, when the user unlinked in between.</returns>
    public async Task<bool> TrySaveAsync(
        Guid userId, long revocationGeneration, string accountId, string displayName, string? email,
        CancellationToken ct = default) =>
        await SaveCoreAsync(userId, revocationGeneration, accountId, displayName, email, ct);

    /// <remarks>Every writer of a user's link first locks the user's revocation row, so two concurrent
    /// saves serialize there instead of racing on the link's unique index.</remarks>
    private async Task<bool> SaveCoreAsync(
        Guid userId, long? expectedGeneration, string accountId, string displayName, string? email, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        long current = await LockRevocationAsync(db, userId, bump: false, ct);
        if (expectedGeneration is long expected && current != expected)
            return false; // disposing the transaction rolls it back

        var existing = await db.UserAtlassianIdentityLinks.SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (existing is null)
        {
            existing = new UserAtlassianIdentityLinkEntity { Id = Guid.NewGuid(), UserId = userId };
            db.UserAtlassianIdentityLinks.Add(existing);
        }

        existing.AccountId = accountId;
        existing.DisplayName = displayName;
        existing.Email = email;
        existing.ConnectedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>Unlinks the user and refuses every sign-in they started before now, in one transaction.</summary>
    /// <returns>Whether a link was removed. Sign-ins are refused either way.</returns>
    public async Task<bool> DeleteAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await LockRevocationAsync(db, userId, bump: true, ct);
        int deleted = await db.UserAtlassianIdentityLinks.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);

        await tx.CommitAsync(ct);
        return deleted > 0;
    }

    /// <summary>Creates the user's revocation row if missing, locks it for the transaction, and returns
    /// its generation — incremented first when <paramref name="bump"/> is set.</summary>
    private static async Task<long> LockRevocationAsync(
        ConnapseIdentityDbContext db, Guid userId, bool bump, CancellationToken ct)
    {
        // ON CONFLICT DO UPDATE takes the row lock even when it writes the same value back, and waits
        // for, then sees, any concurrent writer's committed generation.
        string sql = bump
            ? """
              INSERT INTO user_atlassian_link_revocations AS r (user_id, generation) VALUES (@user_id, 1)
              ON CONFLICT (user_id) DO UPDATE SET generation = r.generation + 1
              RETURNING r.generation AS "Value"
              """
            : """
              INSERT INTO user_atlassian_link_revocations AS r (user_id, generation) VALUES (@user_id, 0)
              ON CONFLICT (user_id) DO UPDATE SET generation = r.generation
              RETURNING r.generation AS "Value"
              """;

        var rows = await db.Database
            .SqlQueryRaw<long>(sql, new NpgsqlParameter("user_id", userId))
            .ToListAsync(ct);
        return rows.Single();
    }
}
