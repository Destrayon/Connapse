using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Runs every per-scheme verifier over the candidates in sequence. Each inner verifier passes the
/// hits that aren't its own, so each one drops only its own unreadable hits. Inner verifiers are
/// called with an uncapped <c>topK</c> so none of them truncates; the composite caps once.
/// </summary>
public sealed class CompositeSearchResultVerifier(IEnumerable<IPerSchemeResultVerifier> inner) : ISearchResultVerifier
{
    private readonly IReadOnlyList<IPerSchemeResultVerifier> _inner = [.. inner];

    public int CandidateMultiplier => _inner.Count == 0 ? 1 : _inner.Max(v => Math.Max(1, v.CandidateMultiplier));

    public async Task<IReadOnlyList<SearchHit>> VerifyAsync(
        IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default)
    {
        IReadOnlyList<SearchHit> survivors = rankedCandidates;
        foreach (var verifier in _inner)
            survivors = await verifier.VerifyAsync(survivors, userId, survivors.Count, ct);

        // An enforcing verifier caps at topK (the contract's "enforcing" branch); when every inner
        // one is pass-through, hand back the untouched pool so AutoCut sees what it saw before.
        return CandidateMultiplier > 1 ? [.. survivors.Take(topK)] : survivors;
    }
}
