namespace Connapse.Core.Utilities;

/// <summary>
/// Decides which access grants are orphaned — held by a directory group, reached by no connection.
/// </summary>
/// <remarks>
/// Pure: the AWS-touching provenance check (is this grant tagged as ours) and the deletion happen
/// around it. The scope test is <see cref="GrantCoverage.IsScopeWithinAllowed"/>, a directional
/// containment — a grant survives only when everything it permits is still within an allowed
/// location.
/// <para>
/// Group-only and tag-blind by design. A user grant is never touched, and provenance requires an AWS
/// call, so it is applied to the (small) candidate set afterwards rather than here. A grant is an
/// orphan when <b>either</b> it belongs to a group that is no longer the configured one (a leftover
/// after the grant group was changed — Connapse only ever creates grants for the configured group,
/// so a managed grant to any other group should not exist) <b>or</b> its scope is broader than any
/// allowed location (a connection was narrowed or removed). Both are stale authorisation.
/// </para>
/// </remarks>
public static class GrantReconciler
{
    /// <summary>
    /// The group grants that should no longer exist: those for a superseded group, or whose scope is
    /// no longer contained in any location in <paramref name="unionLocations"/>.
    /// </summary>
    /// <param name="grants">Every grant in a region (from <c>IAccessGrantsReader.ListAllAsync</c>).</param>
    /// <param name="unionLocations">
    /// The allowed-locations of every S3 connection, unioned. Must be the <b>complete</b> set — an
    /// incomplete union makes still-needed grants look orphaned, so the caller aborts rather than
    /// pass a partial one here.
    /// </param>
    /// <param name="configuredGroupId">
    /// The currently configured grant group. A managed grant held by any other group is stale (the
    /// admin changed the group) and is selected regardless of its scope.
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

            bool isConfiguredGroup = string.Equals(
                grant.Grantee.Id, configuredGroupId, StringComparison.Ordinal);

            // A grant for the configured group whose scope is still fully within an allowed location
            // is the one thing to keep. Everything else — a previous group's grant, or a scope
            // broader than anything now allowed — is an orphan.
            if (isConfiguredGroup && GrantCoverage.IsScopeWithinAllowed(grant.GrantScope, unionLocations))
                continue;

            candidates.Add(grant);
        }

        return new OrphanSelection(candidates);
    }
}

/// <summary>Grants selected for deletion, before the AWS provenance-tag confirmation.</summary>
public record OrphanSelection(IReadOnlyList<AccessGrantDetail> Candidates);
