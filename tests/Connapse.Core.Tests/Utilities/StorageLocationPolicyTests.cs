using System.Text.Json;
using System.Text.Json.Nodes;
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
        //
        // Null, not an empty list. Absent and empty used to arrive here as the same value, and
        // that conflation was the bug: a malformed allowlist whose entries were all dropped
        // became empty, and empty took the grace path.
        StorageLocationPolicy.Evaluate(null, "any-bucket", null)
            .Should().Be(StorageLocationDecision.UnrestrictedByConfiguration);
    }

    /// <summary>
    /// The other side of that line. An allowlist that was declared but permits nothing is a
    /// broken control, not an absent one, and only absent controls fail open.
    /// </summary>
    [Fact]
    public void Evaluate_DeclaredButEmpty_IsDenied()
    {
        StorageLocationPolicy.Evaluate([], "any-bucket", null)
            .Should().Be(StorageLocationDecision.Denied);
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
    public void Evaluate_LocationDifferingOnlyByCase_IsDenied()
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
    public void Evaluate_AllEntriesBlank_IsDenied()
    {
        // A malformed allowlist is not an absent one. Reading a list of blanks as "nothing
        // configured" would let a typo silently permit everything — only a genuinely empty
        // list takes the grace path.
        StorageLocationPolicy.Evaluate(["  ", ""], "any-bucket", null)
            .Should().Be(StorageLocationDecision.Denied);
    }

    [Fact]
    public void Evaluate_BlankEntryAlongsideRealOnes_IsIgnored()
    {
        StorageLocationPolicy.Evaluate(["", "docs"], "payroll", null)
            .Should().Be(StorageLocationDecision.Denied);

        StorageLocationPolicy.Evaluate(["", "docs"], "docs", null)
            .Should().Be(StorageLocationDecision.Allowed);
    }
    // ── Reading the allowlist ──────────────────────────────────────────────
    //
    // One reader, two JSON APIs. The enforcement point works in JsonElement and the create-time
    // preflight in JsonObject, and they used to parse the allowlist separately — which drifted,
    // and drifted the wrong way: the form refused a malformed allowlist while the sync-time
    // check quietly permitted it. Both overloads are asserted to agree.

    private static IReadOnlyList<string>? ReadElement(string json) =>
        StorageLocationPolicy.ReadAllowedLocations(JsonDocument.Parse(json).RootElement);

    private static IReadOnlyList<string>? ReadNode(string json) =>
        StorageLocationPolicy.ReadAllowedLocations(JsonNode.Parse(json)!.AsObject());

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"region":"eu-west-1"}""")]
    public void ReadAllowedLocations_Absent_IsNull(string json)
    {
        ReadElement(json).Should().BeNull("absent is the only thing that may fail open");
        ReadNode(json).Should().BeNull();
    }

    [Fact]
    public void ReadAllowedLocations_WellFormed_ReadsEveryEntry()
    {
        const string json = """{"allowedLocations":["a","b/docs"]}""";

        ReadElement(json).Should().BeEquivalentTo(["a", "b/docs"]);
        ReadNode(json).Should().BeEquivalentTo(["a", "b/docs"]);
    }

    /// <summary>
    /// A non-string element becomes a blank rather than vanishing. Dropping it shrinks the list,
    /// and a list that shrinks to empty is indistinguishable from one that was never declared.
    /// </summary>
    [Theory]
    [InlineData("""{"allowedLocations":[42]}""")]
    [InlineData("""{"allowedLocations":[null]}""")]
    [InlineData("""{"allowedLocations":[{"bucket":"b"}]}""")]
    [InlineData("""{"allowedLocations":[["b"]]}""")]
    [InlineData("""{"allowedLocations":[true]}""")]
    public void ReadAllowedLocations_MalformedEntries_SurviveAsBlanks(string json)
    {
        foreach (var read in new[] { ReadElement(json), ReadNode(json) })
        {
            read.Should().NotBeNull("the allowlist was declared, however badly");
            read.Should().ContainSingle().Which.Should().BeEmpty();

            StorageLocationPolicy.Evaluate(read, "any-bucket", null)
                .Should().Be(StorageLocationDecision.Denied);
        }
    }

    [Fact]
    public void ReadAllowedLocations_MixedEntries_KeepTheGoodOnesAndBlankTheRest()
    {
        const string json = """{"allowedLocations":[42,"real-bucket"]}""";

        foreach (var read in new[] { ReadElement(json), ReadNode(json) })
        {
            read.Should().BeEquivalentTo(["", "real-bucket"]);

            // The surviving entry still governs: the malformed one neither opens the door nor
            // closes it on a location that was legitimately declared.
            StorageLocationPolicy.Evaluate(read, "real-bucket", null)
                .Should().Be(StorageLocationDecision.Allowed);
            StorageLocationPolicy.Evaluate(read, "other-bucket", null)
                .Should().Be(StorageLocationDecision.Denied);
        }
    }

    /// <summary>
    /// Present but not an array at all — the same collapse by a different route, since an array
    /// check that fails used to hand back an empty list.
    /// </summary>
    [Theory]
    [InlineData("""{"allowedLocations":"my-bucket"}""")]
    [InlineData("""{"allowedLocations":42}""")]
    [InlineData("""{"allowedLocations":{"0":"b"}}""")]
    [InlineData("""{"allowedLocations":null}""")]
    public void ReadAllowedLocations_NotAnArray_IsDeclaredAndUnusable(string json)
    {
        foreach (var read in new[] { ReadElement(json), ReadNode(json) })
        {
            StorageLocationPolicy.Evaluate(read, "my-bucket", null)
                .Should().Be(StorageLocationDecision.Denied);
        }
    }

    [Fact]
    public void ReadAllowedLocations_ExplicitlyEmptyArray_IsDeclaredAndPermitsNothing()
    {
        const string json = """{"allowedLocations":[]}""";

        foreach (var read in new[] { ReadElement(json), ReadNode(json) })
        {
            read.Should().NotBeNull().And.BeEmpty();

            StorageLocationPolicy.Evaluate(read, "any-bucket", null)
                .Should().Be(StorageLocationDecision.Denied);
        }
    }
}
