using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Identity.Tests;

/// <summary>
/// A confirmation code claims its link once, however many requests carry it at the same moment.
/// </summary>
/// <remarks>
/// The window is the gap between reading a parked link and removing it, a few instructions wide, so
/// threads released together rarely land inside it. <see cref="RendezvousCache"/> holds every caller
/// in that gap until all of them have read the entry, which makes the overlap certain rather than
/// lucky: a store that treats the removal as its single-use boundary hands the link to every caller.
/// </remarks>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class SamlLinkConfirmationConcurrencyTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public async Task Consume_WhenSimultaneousRequestsAllReadTheCode_YieldsLinkToExactlyOneCaller(int requests)
    {
        using var cache = new RendezvousCache(requests);
        var confirmations = new SamlLinkConfirmations(cache);
        var link = new PendingIdentityLink(Guid.NewGuid(), "dir-1", "person", null);
        string code = confirmations.Start(link);

        // A dedicated thread each: every caller waits for the others, so none may queue behind them.
        Task<PendingIdentityLink?>[] callers = Enumerable.Range(0, requests)
            .Select(_ => Task.Factory.StartNew(
                () => confirmations.Consume(code),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        PendingIdentityLink?[] claimed = await Task.WhenAll(callers);

        claimed.Where(result => result is not null).Should().ContainSingle().Which.Should().Be(link);
    }

    /// <summary>Lets no reader past <see cref="TryGetValue"/> until every expected reader has read.</summary>
    private sealed class RendezvousCache(int readers) : IMemoryCache
    {
        private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(30);

        private readonly MemoryCache _inner = new(new MemoryCacheOptions());
        private readonly Barrier _allHaveRead = new(readers);

        public bool TryGetValue(object key, out object? value)
        {
            bool found = _inner.TryGetValue(key, out value);

            if (!_allHaveRead.SignalAndWait(GiveUpAfter))
                throw new TimeoutException("Not every simultaneous request reached the cache.");

            return found;
        }

        public ICacheEntry CreateEntry(object key) => _inner.CreateEntry(key);

        public void Remove(object key) => _inner.Remove(key);

        public void Dispose()
        {
            _allHaveRead.Dispose();
            _inner.Dispose();
        }
    }
}
