namespace Connapse.Core;

/// <summary>
/// Controls the sweep that deletes S3 Access Grants no connection needs any more.
/// </summary>
/// <remarks>
/// Deletion is the dangerous direction, so this carries a kill switch and a circuit-breaker limit.
/// Both have safe defaults and need no configuration to work; a deployment that wants to pause
/// cleanup or raise the batch ceiling sets them under <see cref="SectionName"/>.
/// </remarks>
public record GrantReconciliationSettings
{
    /// <summary>The settings section this binds to.</summary>
    public const string SectionName = "Identity:GrantReconciliation";

    /// <summary>Whether the reconciler runs at all. A pause switch, not a mode.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The most grants the sweep will delete in one region in one run before refusing.
    /// </summary>
    /// <remarks>
    /// The circuit breaker. A run that wants to delete more than this many grants in a region is far
    /// more likely to be acting on a wrongly-empty view of the connections than to have found that
    /// many genuine orphans, so it aborts and alerts instead — the substitute for a dry-run window.
    /// </remarks>
    public int MaxDeletePerTick { get; init; } = 50;
}
