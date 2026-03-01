using Connapse.Core;
using Connapse.Identity.Data.Entities;

namespace Connapse.Identity.Stores;

public interface ICloudIdentityStore
{
    Task<UserCloudIdentityEntity> CreateAsync(UserCloudIdentityEntity entity, CancellationToken ct = default);
    Task<UserCloudIdentityEntity?> GetByUserAndProviderAsync(Guid userId, CloudProvider provider, CancellationToken ct = default);
    Task<IReadOnlyList<UserCloudIdentityEntity>> ListByUserAsync(Guid userId, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid userId, CloudProvider provider, CancellationToken ct = default);
    Task UpdateLastUsedAsync(Guid id, CancellationToken ct = default);
}
