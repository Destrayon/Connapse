using Connapse.Core;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Connapse.Identity.Services;

/// <summary>
/// Reads and writes which IAM Identity Center user a Connapse user signed in as.
/// </summary>
/// <remarks>
/// Holds no secret, which is why there is no Data Protection here any more. The row records an
/// identity IAM Identity Center attested, and permissions are read later with Connapse's own IAM
/// identity — so there is nothing to encrypt, nothing to rotate, and nothing that expires.
/// </remarks>
public sealed class AwsIdentityLinkStore(
    IDbContextFactory<ConnapseIdentityDbContext> factory,
    TimeProvider timeProvider) : IAwsIdentityLinkReader
{
    /// <inheritdoc />
    public async Task<string?> GetDirectoryUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        var link = await GetAsync(userId, ct);
        return string.IsNullOrWhiteSpace(link?.DirectoryUserId) ? null : link.DirectoryUserId;
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
        string directoryUserId,
        string directoryUserName,
        string? email,
        CancellationToken ct = default)
    {
        // The identity store id is the join key and the user name is what an administrator
        // recognises, so both are required. The email is display data and legitimately absent,
        // which is why it is the only one not guarded.
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryUserName);

        await using var db = await factory.CreateDbContextAsync(ct);

        var existing = await db.UserAwsIdentityLinks
            .SingleOrDefaultAsync(x => x.UserId == userId, ct);

        // Replace rather than add: the unique index would reject a second row anyway, and an
        // upsert keeps ConnectedAt meaning "when this link was established".
        if (existing is null)
        {
            var candidate = new UserAwsIdentityLinkEntity
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                DirectoryUserId = directoryUserId,
                DirectoryUserName = directoryUserName,
                Email = email ?? string.Empty,
                ConnectedAt = timeProvider.GetUtcNow().UtcDateTime,
            };
            db.UserAwsIdentityLinks.Add(candidate);

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
                existing = await db.UserAwsIdentityLinks.SingleAsync(x => x.UserId == userId, ct);
            }
        }

        existing.DirectoryUserId = directoryUserId;
        existing.DirectoryUserName = directoryUserName;
        existing.Email = email ?? string.Empty;
        existing.ConnectedAt = timeProvider.GetUtcNow().UtcDateTime;
        existing.LastUsedAt = null;

        await db.SaveChangesAsync(ct);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>The link, or null when the user has not connected one.</summary>
    public async Task<UserAwsIdentityLinkEntity?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.UserAwsIdentityLinks
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == userId, ct);
    }

    /// <summary>Removes a user's link. False when there was nothing to remove.</summary>
    public async Task<bool> DeleteAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var existing = await db.UserAwsIdentityLinks
            .SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (existing is null)
            return false;

        db.UserAwsIdentityLinks.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Removes a user's link only if it is still the one the caller read, identified by
    /// <paramref name="expectedConnectedAt"/>. False, and the row left untouched, otherwise.
    /// </summary>
    /// <remarks>
    /// For a disconnect that read the link, did some work, and must now delete exactly that row and
    /// no other. An <c>Id</c> comparison would not be enough: a reconnect that raced the disconnect
    /// runs <see cref="SaveAsync"/>, which updates the existing row in place and keeps its
    /// <c>Id</c> — so a plain <see cref="DeleteAsync(Guid, CancellationToken)"/> between the read
    /// and this call would silently throw away the link the user had just re-established.
    /// <c>ConnectedAt</c> is rewritten by every save, so a match here proves the row is still the
    /// one that was read.
    /// </remarks>
    public async Task<bool> DeleteAsync(
        Guid userId, DateTime expectedConnectedAt, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var existing = await db.UserAwsIdentityLinks
            .SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (existing is null || existing.ConnectedAt != expectedConnectedAt)
            return false;

        db.UserAwsIdentityLinks.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
