using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Storage.CloudScope;

/// <summary>The default: no verification. Returns every candidate unchanged — the caller (the search
/// pipeline's own final <c>Take(topK)</c>) applies the cap, so AutoCut still sees the full pool
/// exactly as it would with no verifier in the pipeline at all.</summary>
public sealed class NoOpSearchResultVerifier : ISearchResultVerifier
{
    public int CandidateMultiplier => 1;

    public Task<IReadOnlyList<SearchHit>> VerifyAsync(
        IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default) =>
        Task.FromResult(rankedCandidates);
}
