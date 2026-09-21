using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Connapse.Identity.Services;

/// <summary>
/// Reads and writes which Microsoft Entra identity a Connapse user signed in as.
/// </summary>
/// <remarks>
/// Holds no token, mirroring <see cref="AwsIdentityLinkStore"/>: the row records an identity Entra
/// attested once at link time, and permissions are read later with Connapse's own identity — so
/// there is nothing to encrypt, nothing to rotate, and nothing that expires.
/// </remarks>
public sealed class AzureIdentityLinkStore(
    IDbContextFactory<ConnapseIdentityDbContext> factory,
    TimeProvider timeProvider) : IAzureIdentityLinkReader
{
    /// <inheritdoc />
    public async Task<AzureIdentityRef?> GetLinkAsync(Guid userId, CancellationToken ct = default)
    {
        var link = await GetAsync(userId, ct);
        return link is null ? null : new AzureIdentityRef(link.ObjectId, link.TenantId);
    }

    /// <summary>Stores a user's link, replacing any existing one.</summary>
    /// <remarks>
    /// The read-then-write below is not itself atomic — two concurrent connects for the same user
    /// can both read "no row" and both try to insert. Without the catch below, the unique index on
    /// <c>user_id</c> would reject whichever insert loses that race with an unhandled
    /// <see cref="DbUpdateException"/> instead of one link simply winning. Catching the violation
    /// and falling back to an update keeps this correct without a raw-SQL <c>ON CONFLICT</c>
    /// upsert, which the EF Core InMemory provider (used by this store's unit tests) cannot run.
    /// </remarks>
    public async Task SaveAsync(
        Guid userId,
        string oid,
        string tid,
        string displayName,
        CancellationToken ct = default)
    {
        // The object id and tenant id together are the join key, so both are required. The
        // display name is display data and legitimately absent from some tokens, which is why it
        // is the only one not guarded.
        ArgumentException.ThrowIfNullOrWhiteSpace(oid);
        ArgumentException.ThrowIfNullOrWhiteSpace(tid);

        await using var db = await factory.CreateDbContextAsync(ct);

        var existing = await db.UserAzureIdentityLinks
            .SingleOrDefaultAsync(x => x.UserId == userId, ct);

        // Replace rather than add: the unique index would reject a second row anyway, and an
        // upsert keeps ConnectedAt meaning "when this link was established".
        if (existing is null)
        {
            var candidate = new UserAzureIdentityLinkEntity
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ObjectId = oid,
                TenantId = tid,
                DisplayName = displayName ?? string.Empty,
                ConnectedAt = timeProvider.GetUtcNow().UtcDateTime,
            };
            db.UserAzureIdentityLinks.Add(candidate);

            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Lost a race with a concurrent connect for the same user: the row now exists
                // because someone else's insert landed first between our read above and this
                // write. Detach the insert the unique index rejected — otherwise the next
                // SaveChangesAsync below would try to re-insert it alongside the update — and
                // fall through to update the row that won instead of surfacing the violation.
                db.Entry(candidate).State = EntityState.Detached;
                existing = await db.UserAzureIdentityLinks.SingleAsync(x => x.UserId == userId, ct);
            }
        }

        existing.ObjectId = oid;
        existing.TenantId = tid;
        existing.DisplayName = displayName ?? string.Empty;
        existing.ConnectedAt = timeProvider.GetUtcNow().UtcDateTime;

        await db.SaveChangesAsync(ct);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>The link, or null when the user has not connected one.</summary>
    public async Task<UserAzureIdentityLinkEntity?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.UserAzureIdentityLinks
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == userId, ct);
    }

    /// <summary>Removes a user's link. False when there was nothing to remove.</summary>
    public async Task<bool> DeleteAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var existing = await db.UserAzureIdentityLinks
            .SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (existing is null)
            return false;

        db.UserAzureIdentityLinks.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
