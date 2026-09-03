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
    public void SelectOrphans_ScopeCoveredByOneOfSeveralConnections_IsNotACandidate()
    {
        // The union matters: a bucket removed from one connection is not orphaned if another covers it.
        var sel = GrantReconciler.SelectOrphans(
            [Grant("s3://shared-bucket/*")],
            unionLocations: ["other-bucket", "shared-bucket/team"],
            configuredGroupId: "grp-1");

        sel.Candidates.Should().BeEmpty();
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
    public void SelectOrphans_PreviousGroupButStillCovered_IsLeftAlone()
    {
        var sel = GrantReconciler.SelectOrphans(
            [Grant("s3://my-bucket/docs/*", grp: "grp-OLD")],
            unionLocations: ["my-bucket/docs"],
            configuredGroupId: "grp-1");

        sel.Candidates.Should().BeEmpty();
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
