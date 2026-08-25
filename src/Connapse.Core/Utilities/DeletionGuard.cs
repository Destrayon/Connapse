namespace Connapse.Core.Utilities;

/// <summary>
/// Decides whether a computed deletion set is too large to trust.
/// <para>
/// A reconcile infers deletions from absence: anything indexed but missing from the remote
/// listing is assumed deleted. That inference is only as good as the listing, and a listing
/// can come back empty <em>and successful</em> — a narrowed bucket policy returning 200 OK
/// with zero keys, or a filesystem directory that is temporarily unmounted. Without this
/// check, one such listing deletes every document the source owns.
/// </para>
/// <para>
/// The rule deliberately does not try to decide whether a deletion is <em>correct</em>. For a
/// mirror, a wrong deletion is recoverable — the next sync re-ingests it — so what needs
/// preventing is the catastrophic case, not every false positive.
/// </para>
/// </summary>
public static class DeletionGuard
{
    /// <summary>Deletion sets at or below this size are always applied.</summary>
    /// <remarks>
    /// A floor rather than a pure percentage: on a five-document source, deleting three is
    /// 60% and would trip a percentage rule on completely ordinary tidying.
    /// </remarks>
    public const int AlwaysAllowedCount = 10;

    /// <summary>Proportion of a source's index above which a deletion set is withheld.</summary>
    /// <remarks>
    /// A ceiling rather than a pure count: ten documents out of a hundred thousand is noise,
    /// and an absolute-only rule would block routine churn on any large source.
    /// </remarks>
    public const int WithheldPercent = 10;

    /// <summary>
    /// True when the deletion set should be withheld pending an administrator's approval.
    /// Requires <em>both</em> bounds to be exceeded, so neither degenerate case fires.
    /// </summary>
    public static bool ShouldWithhold(int vanished, int indexed) =>
        vanished > AlwaysAllowedCount && vanished > indexed / (100 / WithheldPercent);
}
