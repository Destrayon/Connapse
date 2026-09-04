using Connapse.Core;
using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Utilities;

[Trait("Category", "Unit")]
public class GrantReconcilerTests
{
    private static AccessGrantDetail Grant(
        string scope, bool group = true, string id = "g", string grp = "grp-1") =>
        new(AccessGrantId: id, AccessGrantArn: "arn:" + id,
            Grantee: new AccessGrantee(IsGroup: group, Id: grp),
            GrantScope: scope, Permission: "READ", AccessGrantsLocationId: "default");

    [Fact]
    public void SelectOrphans_ScopeCoveredByAConnection_IsNotACandidate()
    {
        var sel = GrantReconciler.SelectOrphans(
            [Grant("s3://my-bucket/docs/*")],
            unionLocations: ["my-bucket/docs"],
            configuredGroupId: "grp-1");

        sel.Candidates.Should().BeEmpty();
    }

    [Fact]
    public void SelectOrphans_ScopeCoveredByNoConnection_IsACandidate()
    {
        var sel = GrantReconciler.SelectOrphans(
            [Grant("s3://old-bucket/*")],
            unionLocations: ["my-bucket/docs"],
            configuredGroupId: "grp-1");

        sel.Candidates.Should().ContainSingle().Which.AccessGrantId.Should().Be("g");
    }

    [Fact]
    public void SelectOrphans_GrantNarrowerThanAnAllowedLocation_IsNotACandidate()
    {
        // Grant is a subtree of what a whole-bucket connection allows -> still fully justified.
        var sel = GrantReconciler.SelectOrphans(
            [Grant("s3://shared-bucket/team/*")],
            unionLocations: ["shared-bucket"],
            configuredGroupId: "grp-1");

        sel.Candidates.Should().BeEmpty();
    }

    [Fact]
    public void SelectOrphans_GrantBroaderThanTheNarrowedAllowedLocation_IsACandidate()
    {
        // The narrowing fail-open: a whole-bucket grant is NOT justified once the connection is
        // narrowed to one prefix. Overlap would wrongly keep it; containment revokes it.
        var sel = GrantReconciler.SelectOrphans(
            [Grant("s3://shared-bucket/*")],
            unionLocations: ["other-bucket", "shared-bucket/team"],
            configuredGroupId: "grp-1");

        sel.Candidates.Should().ContainSingle().Which.GrantScope.Should().Be("s3://shared-bucket/*");
    }

    [Fact]
    public void SelectOrphans_PreviousGroupOrphan_IsACandidate()
    {
        // A group that is no longer configured, whose scope nothing covers — a previous-group orphan.
        var sel = GrantReconciler.SelectOrphans(
            [Grant("s3://old-bucket/*", grp: "grp-OLD")],
            unionLocations: ["my-bucket/docs"],
            configuredGroupId: "grp-1");

        sel.Candidates.Should().ContainSingle();
    }

    [Fact]
    public void SelectOrphans_PreviousGroupGrant_IsACandidate_EvenWhenScopeStillCovered()
    {
        // Changing the grant group must revoke the old group's grants, even for a still-connected
        // bucket -- otherwise the old group's members keep seeing the data (stale-authorisation leak).
        var sel = GrantReconciler.SelectOrphans(
            [Grant("s3://my-bucket/docs/*", grp: "grp-OLD")],
            unionLocations: ["my-bucket/docs"],
            configuredGroupId: "grp-1");

        sel.Candidates.Should().ContainSingle();
    }

    [Fact]
    public void SelectOrphans_NonGroupGrant_IsNeverACandidate()
    {
        var sel = GrantReconciler.SelectOrphans(
            [Grant("s3://old-bucket/*", group: false)],
            unionLocations: ["my-bucket/docs"],
            configuredGroupId: "grp-1");

        sel.Candidates.Should().BeEmpty();
    }

    [Fact]
    public void SelectOrphans_SimilarBucketPrefix_DoesNotCount_AsCovered()
    {
        // s3://logs must not read as covered by a connection allowing "logs-archive".
        var sel = GrantReconciler.SelectOrphans(
            [Grant("s3://logs/*")],
            unionLocations: ["logs-archive"],
            configuredGroupId: "grp-1");

        sel.Candidates.Should().ContainSingle();
    }
}
