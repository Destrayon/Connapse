using Connapse.Core;

namespace Connapse.Core.Interfaces;

/// <summary>
/// Post-retrieval per-hit permission verify. Given ranked candidates, returns the subset the searcher
/// may actually read, in rank order, at most <paramref name="topK"/> (dropping unreadable hits and
/// backfilling from lower-ranked survivors). <see cref="CandidateMultiplier"/> tells the pipeline how
/// much to over-fetch so backfill has material.
/// </summary>
public interface ISearchResultVerifier
{
    int CandidateMultiplier { get; }

    Task<IReadOnlyList<SearchHit>> VerifyAsync(
        IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default);
}
