using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Utilities;

/// <summary>
/// Bounds which bucket or blob container a source may name inside a connection whose
/// credential may reach many of them.
/// </summary>
[Trait("Category", "Unit")]
public class StorageLocationPolicyTests
{
    [Fact]
    public void Evaluate_NoLocationsConfigured_IsUnrestricted()
    {
        // Permissive for one release: #350 backfilled existing S3 and Azure containers into
        // connections, none of which declare locations, so denying immediately would break
        // every upgrade.
        StorageLocationPolicy.Evaluate([], "any-bucket", null)
            .Should().Be(StorageLocationDecision.UnrestrictedByConfiguration);
    }

    [Fact]
    public void Evaluate_BucketOnlyEntry_PermitsAnyPrefixWithin()
    {
        StorageLocationPolicy.Evaluate(["docs-bucket"], "docs-bucket", "team/reports/")
            .Should().Be(StorageLocationDecision.Allowed);
    }

    [Fact]
    public void Evaluate_BucketOnlyEntry_DeniesADifferentBucket()
    {
        // The case that matters: the connection's role can read both, and only this stops a
        // source naming the one it should not.
        StorageLocationPolicy.Evaluate(["docs-bucket"], "payroll-bucket", null)
            .Should().Be(StorageLocationDecision.Denied);
    }

    [Fact]
    public void Evaluate_PrefixedEntry_PermitsThatSubtree()
    {
        StorageLocationPolicy.Evaluate(["shared/docs"], "shared", "docs/2026/")
            .Should().Be(StorageLocationDecision.Allowed);
    }

    [Fact]
    public void Evaluate_PrefixedEntry_DeniesOutsideTheSubtree()
    {
        StorageLocationPolicy.Evaluate(["shared/docs"], "shared", "payroll/")
            .Should().Be(StorageLocationDecision.Denied);
    }

    [Fact]
    public void Evaluate_SiblingPrefixSharingAName_IsDenied()
    {
        // "docs-internal" starts with "docs" as a string but is a different subtree — the
        // off-by-slash trap that breaks prefix comparisons.
        StorageLocationPolicy.Evaluate(["shared/docs"], "shared", "docs-internal/")
            .Should().Be(StorageLocationDecision.Denied);
    }

    [Fact]
    public void Evaluate_SiblingBucketSharingAName_IsDenied()
    {
        StorageLocationPolicy.Evaluate(["docs"], "docs-internal", null)
            .Should().Be(StorageLocationDecision.Denied);
    }

    [Fact]
    public void Evaluate_ExactBucketWithNoPrefix_IsAllowed()
    {
        StorageLocationPolicy.Evaluate(["docs-bucket"], "docs-bucket", null)
            .Should().Be(StorageLocationDecision.Allowed);
    }

    [Fact]
    public void Evaluate_IsCaseSensitive()
    {
        // S3 bucket names and Azure container names are both case-sensitive, so matching
        // loosely would admit a name that is not the same resource.
        StorageLocationPolicy.Evaluate(["Docs"], "docs", null)
            .Should().Be(StorageLocationDecision.Denied);
    }

    [Fact]
    public void Evaluate_LeadingAndTrailingSlashes_DoNotChangeTheDecision()
    {
        StorageLocationPolicy.Evaluate(["/shared/docs/"], "shared", "/docs/2026")
            .Should().Be(StorageLocationDecision.Allowed);
    }

    [Fact]
    public void Evaluate_MultipleEntries_MatchesAny()
    {
        StorageLocationPolicy.Evaluate(["a-bucket", "b-bucket/docs"], "b-bucket", "docs/x/")
            .Should().Be(StorageLocationDecision.Allowed);
    }

    [Fact]
    public void Evaluate_BlankEntriesAreIgnoredButDoNotMakeItUnrestricted()
    {
        // A list of blanks must not read as "nothing configured" and silently permit
        // everything — that would turn a typo into an open door.
        StorageLocationPolicy.Evaluate(["  ", ""], "any-bucket", null)
            .Should().Be(StorageLocationDecision.UnrestrictedByConfiguration);

        StorageLocationPolicy.Evaluate(["", "docs"], "payroll", null)
            .Should().Be(StorageLocationDecision.Denied);
    }
}
