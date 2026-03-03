namespace Connapse.Core.Interfaces;

/// <summary>
/// Caches cloud scope discovery results per user per container.
/// </summary>
public interface IConnectorScopeCache
{
    Task<CloudScopeResult?> GetAsync(Guid userId, Guid containerId);
    Task SetAsync(Guid userId, Guid containerId, CloudScopeResult result, TimeSpan ttl);
    void Invalidate(Guid userId, Guid containerId);
}
