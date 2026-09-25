using Connapse.Core.Interfaces;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Connapse.Identity.Services;

/// <summary>Reads and writes which GitHub account a Connapse user signed in as.</summary>
/// <remarks>Holds no token, mirroring <see cref="AzureIdentityLinkStore"/>.</remarks>
public sealed class GitHubIdentityLinkStore(
    IDbContextFactory<ConnapseIdentityDbContext> factory,
    TimeProvider timeProvider) : IGitHubIdentityLinkReader
{
    public async Task<GitHubIdentityRef?> GetLinkAsync(Guid userId, CancellationToken ct = default)
    {
        var link = await GetAsync(userId, ct);
        return link is null ? null : new GitHubIdentityRef(link.GitHubUserId, link.Login);
    }

    public async Task<UserGitHubIdentityLinkEntity?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.UserGitHubIdentityLinks.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct);
    }

    /// <summary>Stores a user's link, replacing any existing one.</summary>
    /// <remarks>Two concurrent connects race on the unique index; the loser updates the winner's row,
    /// as <see cref="AzureIdentityLinkStore.SaveAsync"/> does.</remarks>
    public async Task SaveAsync(Guid userId, long gitHubUserId, string login, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gitHubUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(login);

        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.UserGitHubIdentityLinks.SingleOrDefaultAsync(x => x.UserId == userId, ct);

        if (existing is null)
        {
            var candidate = new UserGitHubIdentityLinkEntity
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                GitHubUserId = gitHubUserId,
                Login = login,
                ConnectedAt = timeProvider.GetUtcNow().UtcDateTime,
            };
            db.UserGitHubIdentityLinks.Add(candidate);

            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.Entry(candidate).State = EntityState.Detached;
                existing = await db.UserGitHubIdentityLinks.SingleAsync(x => x.UserId == userId, ct);
            }
        }

        existing.GitHubUserId = gitHubUserId;
        existing.Login = login;
        existing.ConnectedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.UserGitHubIdentityLinks.SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (existing is null)
            return false;

        db.UserGitHubIdentityLinks.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
