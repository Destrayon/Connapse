using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Storage.Connections;

/// <summary>
/// Stores the credential Connapse acts as, encrypted at rest.
/// </summary>
/// <remarks>
/// Its own DataProtection purpose rather than sharing <c>Connection.v1</c>: purposes bound
/// ciphertext to its use, so a connection secret cannot be read back through this store even by a
/// caller that gets the column contents.
/// <para>
/// The key ring is persisted to the appdata volume with <c>SetApplicationName</c>, so it survives
/// container replacement. Without that, every redeploy would silently empty this table.
/// </para>
/// </remarks>
public class PostgresProviderCredentialStore(
    IDbContextFactory<KnowledgeDbContext> factory,
    IDataProtectionProvider dataProtection) : IProviderCredentialStore
{
    private IDataProtector Protector => dataProtection.CreateProtector("ProviderCredential.v1");

    public async Task<ProviderCredentialInfo?> GetAsync(string provider, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        return await db.ProviderCredentials
            .AsNoTracking()
            .Where(c => c.Provider == provider)
            .Select(c => new ProviderCredentialInfo(
                c.Provider, c.PublicId, c.PrincipalName, c.CreatedAt, c.VerifiedAt))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<string?> GetSecretAsync(string provider, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        string? ciphertext = await db.ProviderCredentials
            .AsNoTracking()
            .Where(c => c.Provider == provider)
            .Select(c => c.SecretProtected)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrEmpty(ciphertext))
            return null;

        try
        {
            return Protector.Unprotect(ciphertext);
        }
        catch (Exception ex)
        {
            // Not swallowed into null. "Nothing stored" and "stored but unreadable" send an
            // administrator to different places, and the second needs saying out loud — otherwise
            // a lost key ring looks like a credential that was never set up.
            throw new ProviderCredentialUnavailableException(provider, ex);
        }
    }

    public async Task<ProviderCredentialInfo> SaveAsync(
        string provider, string publicId, string secret, string? principalName,
        Guid? createdByUserId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        await using var db = await factory.CreateDbContextAsync(ct);

        var existing = await db.ProviderCredentials.FirstOrDefaultAsync(c => c.Provider == provider, ct);
        var now = DateTime.UtcNow;

        if (existing is null)
        {
            existing = new ProviderCredentialEntity { Provider = provider };
            db.ProviderCredentials.Add(existing);
        }

        existing.PublicId = publicId.Trim();
        existing.SecretProtected = Protector.Protect(secret);
        existing.PrincipalName = string.IsNullOrWhiteSpace(principalName) ? null : principalName.Trim();

        // Reset on replacement, not only on first write. The age shown in the UI is the age of the
        // key in use, and a rotated key that reported its predecessor's date would defeat the point
        // of showing it.
        existing.CreatedAt = now;
        existing.CreatedByUserId = createdByUserId;

        // Cleared on replacement. A new key has proved nothing, whatever its predecessor did, and
        // carrying the old timestamp over would make the page report a fresh key as deleted the
        // moment IAM's propagation delay refused its first call.
        existing.VerifiedAt = null;

        await db.SaveChangesAsync(ct);

        return new ProviderCredentialInfo(provider, existing.PublicId, existing.PrincipalName, now);
    }

    public async Task<bool> MarkVerifiedAsync(
        string provider, DateTime when, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // One UPDATE, and only when it changes something. This is called from a page that renders
        // on every visit, so writing unconditionally would turn viewing a status into a write per
        // view -- and the value read back only ever answers "has this ever worked".
        DateTime cutoff = when - VerificationInterval;

        return await db.ProviderCredentials
            .Where(c => c.Provider == provider)
            .Where(c => c.VerifiedAt == null || c.VerifiedAt < cutoff)
            .ExecuteUpdateAsync(c => c.SetProperty(x => x.VerifiedAt, when), ct) > 0;
    }

    /// <summary>How stale the recorded timestamp may get before it is worth another write.</summary>
    /// <remarks>
    /// The reader only asks whether the credential has ever worked, so precision buys nothing.
    /// Refreshing it occasionally keeps it meaningful as a "last seen working" fact without making
    /// a page view a database write.
    /// </remarks>
    private static readonly TimeSpan VerificationInterval = TimeSpan.FromHours(1);

    public async Task<bool> DeleteAsync(string provider, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var existing = await db.ProviderCredentials.FirstOrDefaultAsync(c => c.Provider == provider, ct);
        if (existing is null) return false;

        db.ProviderCredentials.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
