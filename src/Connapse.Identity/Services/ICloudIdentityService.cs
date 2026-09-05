using Connapse.Core;

namespace Connapse.Identity.Services;

public interface ICloudIdentityService
{
    Task<CloudIdentityDto?> GetAsync(Guid userId, CloudProvider provider, CancellationToken ct = default);
    Task<IReadOnlyList<CloudIdentityDto>> ListAsync(Guid userId, CancellationToken ct = default);
    Task<bool> DisconnectAsync(Guid userId, CloudProvider provider, CancellationToken ct = default);
}
