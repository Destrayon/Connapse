using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Identity.Tests;

[Trait("Category", "Unit")]
public class AzureSignInRequestsTests
{
    private static AzureSignInRequests NewStore(out IMemoryCache cache)
    {
        cache = new MemoryCache(new MemoryCacheOptions());
        return new AzureSignInRequests(cache);
    }

    [Fact]
    public void Pending_TakeByState_RemovesAndDropsExpired()
    {
        var store = NewStore(out _);
        store.Add(new AzurePendingSignIn("s1", "v", "n", Guid.NewGuid(), DateTime.UtcNow.AddMinutes(5)));
        store.TakeByState("s1").Should().NotBeNull();
        store.TakeByState("s1").Should().BeNull(); // removed
        store.Add(new AzurePendingSignIn("s2", "v", "n", Guid.NewGuid(), DateTime.UtcNow.AddMinutes(-1)));
        store.TakeByState("s2").Should().BeNull(); // expired
    }

    [Fact]
    public void Pending_AbandonedSignIn_IsNotRetainedInCachePastItsDeadline()
    {
        // Regression: the store used to be a raw dictionary that only removed an entry when its
        // state was redeemed, so a sign-in started and never completed lived for the life of the
        // process. Backing it with the memory cache makes each entry's own deadline the eviction
        // trigger. Assert against the cache's own count, not through TakeByState, so this proves
        // the entry is physically not retained rather than merely filtered on read.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new AzureSignInRequests(cache);

        // A sign-in already past its deadline is not held at all.
        store.Add(new AzurePendingSignIn("abandoned", "v", "n", Guid.NewGuid(), DateTime.UtcNow.AddMilliseconds(-1)));
        cache.Count.Should().Be(0);

        // A live one is held by the cache — so reclaiming it is the cache's job on expiry, not a
        // manual sweep the old dictionary never had.
        store.Add(new AzurePendingSignIn("live", "v", "n", Guid.NewGuid(), DateTime.UtcNow.AddMinutes(5)));
        cache.Count.Should().Be(1);
    }
}
