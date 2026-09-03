namespace Connapse.Core.Utilities;

/// <summary>
/// Decides which access grants are orphaned — held by a directory group, reached by no connection.
/// </summary>
/// <remarks>
/// Pure: the AWS-touching provenance check (is this grant tagged as ours) and the deletion happen
/// around it. The scope test is <see cref="GrantCoverage.IsScopeCovered"/>, the same boundary-aware
/// overlap the coverage reporter uses in the create direction, so "granted" and "orphaned" cannot
/// disagree.
/// <para>
/// Group-only and tag-blind by design. A user grant is never touched, and provenance requires an AWS
/// call, so it is applied to the (small) candidate set afterwards rather than here. Both the current
/// group's orphans and a superseded group's orphans are selected — an orphaned group grant is an
/// orphaned group grant whichever group holds it; the only question that matters is whether any
/// connection still reaches its scope.
/// </para>
/// </remarks>
public static class GrantReconciler
{
    /// <summary>
    /// The group grants whose scope no location in <paramref name="unionLocations"/> reaches.
    /// </summary>
    /// <param name="grants">Every grant in a region (from <c>IAccessGrantsReader.ListAllAsync</c>).</param>
    /// <param name="unionLocations">
    /// The allowed-locations of every S3 connection, unioned. Must be the <b>complete</b> set — an
    /// incomplete union makes still-needed grants look orphaned, so the caller aborts rather than
    /// pass a partial one here.
    /// </param>
    /// <param name="configuredGroupId">
    /// The currently configured grant group. Unused for the orphan decision itself (a superseded
    /// group's orphans are deleted too), but carried so callers that want to treat the two cases
    /// differently can.
    /// </param>
    public static OrphanSelection SelectOrphans(
        IReadOnlyList<AccessGrantDetail> grants,
        IReadOnlyList<string> unionLocations,
        string configuredGroupId)
    {
        var candidates = new List<AccessGrantDetail>();

        foreach (var grant in grants)
        {
            // Never touch anything but a directory-group grant.
            if (!grant.Grantee.IsGroup || string.IsNullOrWhiteSpace(grant.Grantee.Id))
                continue;

            // Orphaned = its scope overlaps no allowed location across every connection.
            if (GrantCoverage.IsScopeCovered(grant.GrantScope, unionLocations))
                continue;

            candidates.Add(grant);
        }

        return new OrphanSelection(candidates);
    }
}

/// <summary>Grants selected for deletion, before the AWS provenance-tag confirmation.</summary>
public record OrphanSelection(IReadOnlyList<AccessGrantDetail> Candidates);
