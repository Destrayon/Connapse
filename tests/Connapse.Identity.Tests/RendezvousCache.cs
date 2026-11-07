using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Identity.Tests;

/// <summary>
/// Holds the first <paramref name="readers"/> reads until all of them have read, which puts every
/// simultaneous request inside the gap between reading a one-time entry and removing it.
/// </summary>
/// <remarks>
/// That gap is a few instructions wide, so threads merely released together rarely land inside it.
/// Holding them there makes the overlap certain rather than lucky: a store that treats the removal
/// as its single-use boundary hands the entry to every caller. Reads after the first
/// <paramref name="readers"/> pass straight through, because the winner of a claim may go on to read
/// something else, such as a revocation, with nobody left to wait for.
/// </remarks>
internal sealed class RendezvousCache(int readers) : IMemoryCache
{
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(30);

    private readonly MemoryCache _inner = new(new MemoryCacheOptions());
    private readonly Barrier _allHaveRead = new(readers);
    private int _reads;

    public bool TryGetValue(object key, out object? value)
    {
        bool found = _inner.TryGetValue(key, out value);

        if (Interlocked.Increment(ref _reads) <= readers && !_allHaveRead.SignalAndWait(GiveUpAfter))
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

    /// <summary>Runs <paramref name="request"/> once per reader, at the same moment.</summary>
    /// <remarks>A dedicated thread each: every caller waits for the others, so none may queue behind them.</remarks>
    public Task<T[]> SendSimultaneously<T>(Func<T> request) =>
        Task.WhenAll(Enumerable.Range(0, readers).Select(_ => Task.Factory.StartNew(
            request,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)));
}
