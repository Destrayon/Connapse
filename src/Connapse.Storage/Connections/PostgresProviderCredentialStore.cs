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

    public async Task<ProviderCredentialStatus?> GetStatusAsync(string provider, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        return await db.ProviderCredentials
            .AsNoTracking()
            .Where(c => c.Provider == provider)
            .Select(c => new ProviderCredentialStatus(c.CreatedAt, c.VerifiedAt))
            .FirstOrDefaultAsync(ct);
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

    public async Task<RolesAnywhereConfig?> GetRolesAnywhereAsync(string provider, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var row = await db.ProviderCredentials
            .AsNoTracking()
            .Where(c => c.Provider == provider)
            .Select(c => new { c.CertificatePem, c.TrustAnchorArn, c.ProfileArn, c.RoleArn, c.Region })
            .FirstOrDefaultAsync(ct);

        // TrustAnchorArn is the mode signal: absent means this row is not a Roles Anywhere config.
        if (row is null || string.IsNullOrEmpty(row.TrustAnchorArn))
            return null;

        return new RolesAnywhereConfig(
            row.CertificatePem ?? string.Empty, row.TrustAnchorArn, row.ProfileArn ?? string.Empty,
            row.RoleArn ?? string.Empty, row.Region ?? string.Empty);
    }

    public async Task<RolesAnywhereCredentialMaterial?> GetRolesAnywhereMaterialAsync(
        string provider, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // One query, one row snapshot: config and key ciphertext come from the same read, so a
        // rotation landing between two separate queries can never pair an old cert with a new key.
        var row = await db.ProviderCredentials
            .AsNoTracking()
            .Where(c => c.Provider == provider)
            .Select(c => new
            {
                c.CertificatePem, c.TrustAnchorArn, c.ProfileArn, c.RoleArn, c.Region,
                c.PrivateKeyProtected,
            })
            .FirstOrDefaultAsync(ct);

        // TrustAnchorArn is the mode signal: absent means this row is not a Roles Anywhere config.
        if (row is null || string.IsNullOrEmpty(row.TrustAnchorArn))
            return null;

        if (string.IsNullOrEmpty(row.PrivateKeyProtected))
        {
            // A Roles Anywhere row with no key ciphertext is corruption, not "nothing configured" —
            // the mode signal (TrustAnchorArn) says this row is Roles Anywhere.
            throw new ProviderCredentialUnavailableException(
                provider, new InvalidOperationException(
                    "The stored Roles Anywhere row has no private key ciphertext."));
        }

        string privateKeyPem;
        try
        {
            privateKeyPem = Protector.Unprotect(row.PrivateKeyProtected);
        }
        catch (Exception ex)
        {
            throw new ProviderCredentialUnavailableException(provider, ex);
        }

        var config = new RolesAnywhereConfig(
            row.CertificatePem ?? string.Empty, row.TrustAnchorArn, row.ProfileArn ?? string.Empty,
            row.RoleArn ?? string.Empty, row.Region ?? string.Empty);

        return new RolesAnywhereCredentialMaterial(config, privateKeyPem);
    }

    public async Task<ProviderCredentialInfo> SaveRolesAnywhereAsync(
        string provider, RolesAnywhereConfig config, string privateKeyPem, string? principalName,
        Guid? createdByUserId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.CertificatePem);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.TrustAnchorArn);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.ProfileArn);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.RoleArn);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Region);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);

        await using var db = await factory.CreateDbContextAsync(ct);

        var existing = await db.ProviderCredentials.FirstOrDefaultAsync(c => c.Provider == provider, ct);
        var now = DateTime.UtcNow;

        if (existing is null)
        {
            existing = new ProviderCredentialEntity { Provider = provider };
            db.ProviderCredentials.Add(existing);
        }

        existing.CertificatePem = config.CertificatePem;
        existing.PrivateKeyProtected = Protector.Protect(privateKeyPem);
        existing.TrustAnchorArn = config.TrustAnchorArn;
        existing.ProfileArn = config.ProfileArn;
        existing.RoleArn = config.RoleArn;
        existing.Region = config.Region;
        existing.PrincipalName = string.IsNullOrWhiteSpace(principalName) ? null : principalName.Trim();

        existing.CreatedAt = now;
        existing.CreatedByUserId = createdByUserId;
        existing.VerifiedAt = null;

        await db.SaveChangesAsync(ct);

        return new ProviderCredentialInfo(provider, existing.PrincipalName, now);
    }
}
