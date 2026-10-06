using System.Collections.Concurrent;

namespace Connapse.Search.Reranking;

/// <summary>
/// Remembers reranker endpoints that could not be reached, so searches skip them for a while instead
/// of each paying a retry, a connection failure and an error log (#668).
/// </summary>
/// <remarks>
/// The HTTP client's circuit breaker does this for busy deployments, but it needs several calls a
/// minute before it opens; a self-hosted install whose reranker container isn't running searches
/// less often than that and would otherwise pay on every search. After <see cref="Cooldown"/> one
/// search tries the endpoint again, so a service that comes back is used again without a restart.
/// </remarks>
public sealed class RerankerAvailability(TimeProvider clock)
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _skipUntil = new(StringComparer.Ordinal);

    /// <summary>True while the endpoint is within the cooldown after a failure to reach it.</summary>
    public bool IsSkipped(string endpoint) =>
        _skipUntil.TryGetValue(endpoint, out DateTimeOffset until) && clock.GetUtcNow() < until;

    /// <summary>Starts or extends the endpoint's cooldown. True when this failure starts an outage.</summary>
    public bool MarkUnreachable(string endpoint)
    {
        bool started = true;
        _skipUntil.AddOrUpdate(endpoint, _ => clock.GetUtcNow() + Cooldown, (_, _) =>
        {
            started = false;
            return clock.GetUtcNow() + Cooldown;
        });
        return started;
    }

    /// <summary>Ends the endpoint's outage. True when there was one.</summary>
    public bool MarkReachable(string endpoint) => _skipUntil.TryRemove(endpoint, out _);
}
