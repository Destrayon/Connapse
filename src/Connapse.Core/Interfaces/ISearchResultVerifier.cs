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

/// <summary>
/// A verifier that owns one hit scheme and passes every other scheme's hits through. Implementations
/// register against this interface; only the composite is registered as <see cref="ISearchResultVerifier"/>.
/// <see cref="VerifyScopedAsync"/> additionally reports whether the call just made capped (enforced), which
/// is what the composite uses to decide whether to cap; it must not infer that from <see cref="ISearchResultVerifier.CandidateMultiplier"/>.
/// Implementations typically make <c>VerifyAsync</c> return <c>(await VerifyScopedAsync(...)).Hits</c>.
/// </summary>
public interface IPerSchemeResultVerifier : ISearchResultVerifier
{
    Task<PerSchemeVerification> VerifyScopedAsync(
        IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default);
}

/// <summary>The readable hits, and whether this verification call enforced (so the final result must be capped at topK).</summary>
public sealed record PerSchemeVerification(IReadOnlyList<SearchHit> Hits, bool Enforced);
