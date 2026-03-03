using Connapse.Core;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Connapse.Identity.Stores;

public class PostgresCloudIdentityStore(
    ConnapseIdentityDbContext dbContext,
    ILogger<PostgresCloudIdentityStore> logger) : ICloudIdentityStore
{
    public async Task<UserCloudIdentityEntity> CreateAsync(UserCloudIdentityEntity entity, CancellationToken ct)
    {
        dbContext.UserCloudIdentities.Add(entity);
        await dbContext.SaveChangesAsync(ct);
        logger.LogInformation("Created {Provider} cloud identity for user {UserId}", entity.Provider, entity.UserId);
        return entity;
    }

    public async Task<UserCloudIdentityEntity?> GetByUserAndProviderAsync(Guid userId, CloudProvider provider, CancellationToken ct)
    {
        return await dbContext.UserCloudIdentities
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.UserId == userId && e.Provider == provider, ct);
    }

    public async Task<IReadOnlyList<UserCloudIdentityEntity>> ListByUserAsync(Guid userId, CancellationToken ct)
    {
        return await dbContext.UserCloudIdentities
            .AsNoTracking()
            .Where(e => e.UserId == userId)
            .OrderBy(e => e.Provider)
            .ToListAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid userId, CloudProvider provider, CancellationToken ct)
    {
        var entity = await dbContext.UserCloudIdentities
            .FirstOrDefaultAsync(e => e.UserId == userId && e.Provider == provider, ct);
        if (entity is null) return false;

        dbContext.UserCloudIdentities.Remove(entity);
        await dbContext.SaveChangesAsync(ct);
        logger.LogInformation("Deleted {Provider} cloud identity for user {UserId}", provider, userId);
        return true;
    }

    public async Task UpdateLastUsedAsync(Guid id, CancellationToken ct)
    {
        await dbContext.UserCloudIdentities
            .Where(e => e.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.LastUsedAt, DateTime.UtcNow), ct);
    }
}
