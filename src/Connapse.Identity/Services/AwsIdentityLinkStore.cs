using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Connapse.Identity.Services;

/// <summary>
/// Reads and writes a user's connected AWS identity, and is the only code that sees the refresh
/// token in plaintext.
/// </summary>
/// <remarks>
/// Encryption uses the Data Protection key ring already configured in <c>Program.cs</c>, which is
/// persisted to the <c>appdata</c> volume — so a stored token survives a container restart. It is
/// worth knowing what that does and does not buy: the ring is not itself encrypted at rest, so this
/// protects a token against someone reading the database, not against someone with the volume.
/// <para>
/// The purpose string is deliberately specific. Data Protection derives a distinct key from it, so
/// a payload protected here cannot be unprotected by any other part of the application even though
/// they share a key ring.
/// </para>
/// </remarks>
public sealed class AwsIdentityLinkStore(
    IDbContextFactory<ConnapseIdentityDbContext> factory,
    IDataProtectionProvider protectionProvider,
    TimeProvider timeProvider)
{
    private const string Purpose = "Connapse.AwsIdentityLink.RefreshToken.v1";

    private IDataProtector Protector => protectionProvider.CreateProtector(Purpose);

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
        Guid userId, string email, string refreshToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

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
                Email = email,
                ProtectedRefreshToken = Protector.Protect(refreshToken),
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

        existing.Email = email;
        existing.ProtectedRefreshToken = Protector.Protect(refreshToken);
        existing.ConnectedAt = timeProvider.GetUtcNow().UtcDateTime;
        existing.LastUsedAt = null;

        await db.SaveChangesAsync(ct);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>The link's metadata, or null when the user has not connected one.</summary>
    public async Task<UserAwsIdentityLinkEntity?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.UserAwsIdentityLinks
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == userId, ct);
    }

    /// <summary>The plaintext refresh token, or null when there is no usable link.</summary>
    /// <remarks>
    /// Returns null rather than throwing when the stored payload cannot be unprotected. That
    /// happens for one real reason — the key ring was lost or rotated beyond its retention — and a
    /// caller cannot do anything about it except treat the link as absent and ask the user to
    /// reconnect, which is exactly what null already means here.
    /// <para>
    /// Collapsing "no link" and "link present but unreadable" into the same null is fine for a
    /// caller that only wants a usable token. A caller that must tell those two apart — a revoke
    /// path needs to know whether it is skipping a real live token — should call <see cref="GetAsync"/>
    /// once and pass the result to <see cref="TryUnprotectToken"/> instead, so the distinction is not
    /// lost and no second round trip is needed to make it.
    /// </para>
    /// </remarks>
    public async Task<string?> GetRefreshTokenAsync(Guid userId, CancellationToken ct = default)
    {
        var link = await GetAsync(userId, ct);
        return link is null ? null : TryUnprotectToken(link);
    }

    /// <summary>
    /// Unprotects the token on an already-fetched link (from <see cref="GetAsync"/>), with no DB
    /// access of its own. Null means the row exists but its token could not be decrypted — the
    /// key-ring-lost-or-rotated case described on <see cref="GetRefreshTokenAsync"/>.
    /// </summary>
    public string? TryUnprotectToken(UserAwsIdentityLinkEntity link)
    {
        ArgumentNullException.ThrowIfNull(link);

        try
        {
            return Protector.Unprotect(link.ProtectedRefreshToken);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
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
    /// Removes a user's link only if its <see cref="UserAwsIdentityLinkEntity.ProtectedRefreshToken"/>
    /// still matches <paramref name="expectedProtectedRefreshToken"/>. False, and the row left
    /// untouched, when there is no row or when the token no longer matches.
    /// </summary>
    /// <remarks>
    /// For a caller (a disconnect) that read the link, revoked its token elsewhere, and must now
    /// delete exactly that row and no other. An <c>Id</c> comparison would not be enough: a
    /// reconnect that raced the disconnect runs <see cref="SaveAsync"/>, which updates the
    /// existing row in place and keeps its <c>Id</c> — so a plain <c>DeleteAsync(userId)</c>
    /// between the read and this call would remove the new link while its refresh token stays live
    /// at the provider, which is exactly the outcome a disconnect exists to prevent. The protected
    /// token value changes on every <see cref="SaveAsync"/>, so a match here proves the row is
    /// still the one the caller revoked.
    /// </remarks>
    public async Task<bool> DeleteAsync(
        Guid userId, string expectedProtectedRefreshToken, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var existing = await db.UserAwsIdentityLinks
            .SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (existing is null || existing.ProtectedRefreshToken != expectedProtectedRefreshToken)
            return false;

        db.UserAwsIdentityLinks.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
