using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Storage.CloudScope;

public class ConnectorScopeCache(IMemoryCache cache) : IConnectorScopeCache
{
    private static string Key(Guid userId, Guid containerId) =>
        $"scope:{userId}:{containerId}";

    public Task<CloudScopeResult?> GetAsync(Guid userId, Guid containerId)
    {
        cache.TryGetValue(Key(userId, containerId), out CloudScopeResult? result);
        return Task.FromResult(result);
    }

    public Task SetAsync(Guid userId, Guid containerId, CloudScopeResult result, TimeSpan ttl)
    {
        cache.Set(Key(userId, containerId), result, ttl);
        return Task.CompletedTask;
    }

    public void Invalidate(Guid userId, Guid containerId) =>
        cache.Remove(Key(userId, containerId));
}
