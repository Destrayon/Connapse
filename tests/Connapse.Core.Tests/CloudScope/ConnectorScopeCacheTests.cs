using Connapse.Core;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Core.Tests.CloudScope;

[Trait("Category", "Unit")]
public class ConnectorScopeCacheTests
{
    private readonly ConnectorScopeCache _cache = new(new MemoryCache(new MemoryCacheOptions()));
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _containerId = Guid.NewGuid();

    [Fact]
    public async Task GetAsync_BeforeSet_ReturnsNull()
    {
        var result = await _cache.GetAsync(_userId, _containerId);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_AfterSet_ReturnsCachedValue()
    {
        var scope = CloudScopeResult.FullAccess();
        await _cache.SetAsync(_userId, _containerId, scope, TimeSpan.FromMinutes(5));

        var result = await _cache.GetAsync(_userId, _containerId);
        result.Should().NotBeNull();
        result!.HasAccess.Should().BeTrue();
        result.AllowedPrefixes.Should().Contain("/");
    }

    [Fact]
    public async Task Invalidate_RemovesEntry()
    {
        await _cache.SetAsync(_userId, _containerId, CloudScopeResult.FullAccess(), TimeSpan.FromMinutes(5));

        _cache.Invalidate(_userId, _containerId);

        var result = await _cache.GetAsync(_userId, _containerId);
        result.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_DifferentUsers_IndependentEntries()
    {
        var user2 = Guid.NewGuid();
        await _cache.SetAsync(_userId, _containerId, CloudScopeResult.FullAccess(), TimeSpan.FromMinutes(5));
        await _cache.SetAsync(user2, _containerId, CloudScopeResult.Deny("no access"), TimeSpan.FromMinutes(5));

        var result1 = await _cache.GetAsync(_userId, _containerId);
        var result2 = await _cache.GetAsync(user2, _containerId);

        result1!.HasAccess.Should().BeTrue();
        result2!.HasAccess.Should().BeFalse();
    }
}
