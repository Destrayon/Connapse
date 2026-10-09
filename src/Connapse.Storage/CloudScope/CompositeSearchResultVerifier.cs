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
        bool enforced = false;
        foreach (var verifier in _inner)
        {
            PerSchemeVerification result = await verifier.VerifyScopedAsync(survivors, userId, survivors.Count, ct);
            survivors = result.Hits;
            enforced |= result.Enforced;
        }

        // Any verifier that enforced on this call caps at topK (the contract's "enforcing" branch),
        // whatever its over-fetch multiplier; when none did, hand back the untouched pool so AutoCut
        // sees what it saw before.
        return enforced ? [.. survivors.Take(topK)] : survivors;
    }
}
