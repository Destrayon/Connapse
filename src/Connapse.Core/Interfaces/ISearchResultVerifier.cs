using Connapse.Core;

namespace Connapse.Core.Interfaces;

/// <summary>
/// Post-retrieval per-hit permission verify. Returns the readable hits in rank order. An enforcing
/// verifier drops unreadable hits and caps the result at <paramref name="topK"/>, backfilling from
/// lower-ranked candidates; a pass-through/no-op verifier returns all candidates unchanged and lets
/// the caller apply the final <paramref name="topK"/> cap. <see cref="CandidateMultiplier"/> tells
/// the pipeline how much to over-fetch so backfill has material.
/// </summary>
public interface ISearchResultVerifier
{
    int CandidateMultiplier { get; }

    Task<IReadOnlyList<SearchHit>> VerifyAsync(
        IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default);
}
