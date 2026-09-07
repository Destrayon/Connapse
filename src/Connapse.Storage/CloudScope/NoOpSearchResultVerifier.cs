using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Storage.CloudScope;

/// <summary>The default: no Azure verification. Returns the top <c>topK</c> unchanged.</summary>
public sealed class NoOpSearchResultVerifier : ISearchResultVerifier
{
    public int CandidateMultiplier => 1;

    public Task<IReadOnlyList<SearchHit>> VerifyAsync(
        IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SearchHit>>(rankedCandidates.Take(topK).ToList());
}
