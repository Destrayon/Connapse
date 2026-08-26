using System.Text.Json;
using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Utilities;

/// <summary>
/// The IAM policy Connapse offers so an operator can narrow a credential to exactly what a source
/// needs, and the compose fragment that lets the container see any credential at all.
/// </summary>
/// <remarks>
/// Text destined for other systems, neither of which Connapse applies. Both fail in ways a compiler
/// cannot see — IAM rejects a malformed document, and a bad ARN silently matches nothing — so the
/// assertions are about the shape AWS requires rather than about the string.
/// </remarks>
[Trait("Category", "Unit")]
public class S3SetupPolicyTests
{
    private static JsonElement Parse(string policy) =>
        JsonDocument.Parse(policy).RootElement;

    private static JsonElement Statement(string policy, string sid) =>
        Parse(policy).GetProperty("Statement").EnumerateArray()
            .Single(s => s.GetProperty("Sid").GetString() == sid);

    [Fact]
    public void ForBucket_IsValidJsonWithAPolicyVersion()
    {
        var root = Parse(S3SetupPolicy.ForBucket("my-bucket"));

        root.GetProperty("Version").GetString().Should().Be("2012-10-17");
        root.GetProperty("Statement").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public void ForBucket_NoPrefix_OmitsTheConditionRatherThanNullingIt()
    {
        // Caught by generating one and reading it: the entry serialised as "Condition": null,
        // which IAM rejects outright. WhenWritingNull governs object properties, not dictionary
        // entries, so the key has to be absent rather than empty.
        string policy = S3SetupPolicy.ForBucket("my-bucket");

        policy.Should().NotContain("null");
        Statement(policy, "ConnapseListBucket").TryGetProperty("Condition", out _)
            .Should().BeFalse("an unscoped grant has no prefix to condition on");
    }

    [Fact]
    public void ForBucket_SplitsBucketAndObjectActionsAcrossTwoStatements()
    {
        // s3:ListBucket is a bucket-level action and s3:GetObject an object-level one. Granting
        // both against a single resource is the classic mistake: against the bucket ARN alone,
        // every object read is denied, and the policy looks correct while nothing can be read.
        string policy = S3SetupPolicy.ForBucket("my-bucket", "docs");

        Statement(policy, "ConnapseListBucket").GetProperty("Resource").GetString()
            .Should().Be("arn:aws:s3:::my-bucket");

        Statement(policy, "ConnapseReadObjects").GetProperty("Resource").GetString()
            .Should().Be("arn:aws:s3:::my-bucket/docs/*");
    }

    [Fact]
    public void ForBucket_GrantsNothingBeyondReading()
    {
        // Connapse has no write surface against a source at all — IConnector does not expose one —
        // so a policy suggesting otherwise would ask for authority the product cannot use.
        string policy = S3SetupPolicy.ForBucket("my-bucket");

        var actions = Parse(policy).GetProperty("Statement").EnumerateArray()
            .SelectMany(s => s.GetProperty("Action").EnumerateArray())
            .Select(a => a.GetString())
            .ToList();

        actions.Should().BeEquivalentTo([.. S3SetupPolicy.ReadActions, "s3:GetBucketLocation"]);
        actions.Should().NotContain(a => a!.Contains("Put") || a.Contains("Delete") || a.Contains("*"));
    }

    [Fact]
    public void ForBucket_LocatesTheBucketWithoutTheListingCondition()
    {
        // GetBucketLocation does not understand s3:prefix. Folded in beside ListBucket it would be
        // denied on every grant naming a folder -- and S3Discovery asks for the region before its
        // first read, so a prefixed grant would fail at the step before the one it was written for.
        string policy = S3SetupPolicy.ForBucket("my-bucket", "docs");

        var locate = Statement(policy, "ConnapseLocateBucket");
        locate.TryGetProperty("Condition", out _).Should().BeFalse();
        locate.GetProperty("Resource").GetString().Should().Be("arn:aws:s3:::my-bucket");
    }

    [Fact]
    public void ForBucket_WithPrefix_BoundsListingAsWellAsReading()
    {
        // Without the condition the grant still bounds *reading* to the prefix, but lets the holder
        // enumerate every key in the bucket — more than the source needs, and more than the
        // operator agreed to by typing a prefix.
        var condition = Statement(S3SetupPolicy.ForBucket("my-bucket", "docs"), "ConnapseListBucket")
            .GetProperty("Condition").GetProperty("StringLike").GetProperty("s3:prefix");

        condition.EnumerateArray().Single().GetString().Should().Be("docs/*");
    }

    [Theory]
    [InlineData("docs", "docs/")]
    [InlineData("/docs", "docs/")]
    [InlineData("docs/", "docs/")]
    [InlineData("  /docs/sub  ", "docs/sub/")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalisePrefix_ProducesWhatAnArnWants(string? given, string expected)
    {
        // A leading slash yields bucket//docs*, which matches nothing: S3 keys have no leading
        // slash. A missing trailing slash makes "docs" also match "docs-archive/", quietly
        // widening the grant past the folder that was meant.
        S3SetupPolicy.NormalisePrefix(given).Should().Be(expected);
    }

    [Fact]
    public void ForBucket_PrefixWithoutTrailingSlash_DoesNotLeakIntoASiblingFolder()
    {
        string policy = S3SetupPolicy.ForBucket("my-bucket", "docs");

        Statement(policy, "ConnapseReadObjects").GetProperty("Resource").GetString()
            .Should().NotBe("arn:aws:s3:::my-bucket/docs*",
                "that also matches docs-archive/, which the operator did not grant");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ForBucket_WithoutABucket_Throws(string? bucket)
    {
        // A policy naming arn:aws:s3::: with no bucket is not a narrower grant, it is a malformed
        // one — better to fail here than to hand someone a document IAM will reject.
        FluentActions.Invoking(() => S3SetupPolicy.ForBucket(bucket!))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CredentialInstructions_AskForAFileInAFolder_NotAComposeEdit()
    {
        // The point of the whole arrangement. An operator who has to edit docker-compose.yml to
        // make a feature work concludes the feature does not work, so the compose file mounts
        // ./aws unconditionally and this only has to say where to put the file.
        string text = S3SetupPolicy.CredentialInstructions();

        text.Should().Contain(S3SetupPolicy.CredentialFolder);
        text.Should().NotContain("volumes:", "editing compose is what this exists to avoid");
        text.Should().NotContain("services:");
    }

    [Fact]
    public void CredentialInstructions_OfferTheEnvVariableAsTheAlternative()
    {
        // For anyone who would rather point at the profile they already keep than copy it. One
        // line, in a file that already exists for exactly this purpose.
        string text = S3SetupPolicy.CredentialInstructions();

        text.Should().Contain(S3SetupPolicy.CredentialDirVariable);
        text.Should().Contain(".env");
    }

    [Fact]
    public void CredentialInstructions_NameTheContainerUsersHome()
    {
        // The image runs as the non-root user "app" with HOME=/home/app, verified against the
        // running container. Mounting to /root would put the profile somewhere the process cannot
        // read, and the SDK would report no credentials with nothing to explain why.
        S3SetupPolicy.CredentialInstructions().Should().Contain("/home/app/.aws");
    }

    [Fact]
    public void CredentialInstructions_DoNotSuggestPastingKeys()
    {
        // Nothing is copied into Connapse, and the one place people copy from must not imply
        // otherwise.
        string text = S3SetupPolicy.CredentialInstructions();

        text.Should().NotContain("AWS_SECRET_ACCESS_KEY");
        text.Should().NotContain("AWS_ACCESS_KEY_ID");
    }

    // -- Several buckets at once ----------------------------------------------------

    [Fact]
    public void ForBuckets_TwoBuckets_ParseAsOneDocument()
    {
        // The bug this replaces: a policy was generated per bucket and the results joined with
        // blank lines. Two top-level JSON objects are not a policy, IAM rejects the paste, and the
        // UI presented it as a single thing to attach. Parsing is the assertion that matters --
        // string checks would have passed on the broken version.
        string policy = S3SetupPolicy.ForBuckets(["docs-bucket", "shared-bucket/team"]);

        var root = Parse(policy);
        root.GetProperty("Version").GetString().Should().Be("2012-10-17");
        root.GetProperty("Statement").GetArrayLength().Should().Be(6, "three statements per bucket");
    }

    [Fact]
    public void ForBuckets_GivesEveryStatementItsOwnSid()
    {
        // Sids must be unique within a document. Reusing ConnapseListBucket across buckets makes a
        // document IAM refuses, which is the same failure in a subtler form.
        var sids = Parse(S3SetupPolicy.ForBuckets(["a-bucket", "b-bucket", "c-bucket"]))
            .GetProperty("Statement").EnumerateArray()
            .Select(x => x.GetProperty("Sid").GetString())
            .ToList();

        sids.Should().OnlyHaveUniqueItems();
        sids.Should().HaveCount(9);
    }

    [Fact]
    public void ForBuckets_CarriesThePrefixFromEachEntry()
    {
        // An allowlist entry may name a bucket alone or a bucket and a prefix, and the two must not
        // be flattened together: one grants the whole bucket, the other one folder.
        var resources = Parse(S3SetupPolicy.ForBuckets(["wide-bucket", "narrow-bucket/team"]))
            .GetProperty("Statement").EnumerateArray()
            .Where(x => x.GetProperty("Sid").GetString()!.StartsWith("ConnapseReadObjects"))
            .Select(x => x.GetProperty("Resource").GetString())
            .ToList();

        resources.Should().Equal(
            "arn:aws:s3:::wide-bucket/*",
            "arn:aws:s3:::narrow-bucket/team/*");
    }

    [Fact]
    public void ForBuckets_ScopesListingOnlyWhereAPrefixWasGiven()
    {
        var statements = Parse(S3SetupPolicy.ForBuckets(["wide-bucket", "narrow-bucket/team"]))
            .GetProperty("Statement").EnumerateArray()
            .Where(x => x.GetProperty("Sid").GetString()!.StartsWith("ConnapseListBucket"))
            .ToList();

        statements[0].TryGetProperty("Condition", out _).Should().BeFalse("no prefix was given");
        statements[1].GetProperty("Condition").GetProperty("StringLike")
            .GetProperty("s3:prefix").EnumerateArray().Single().GetString().Should().Be("team/*");
    }

    public static TheoryData<string[]> NothingUsable()
    {
        var data = new TheoryData<string[]>();
        data.Add([]);
        data.Add([""]);
        data.Add(["   ", "/"]);
        return data;
    }

    [Theory]
    [MemberData(nameof(NothingUsable))]
    public void ForBuckets_WithNothingUsable_Throws(string[] locations)
    {
        // Entries are typed freehand, so a half-written allowlist reaches here. Throwing lets the
        // caller show nothing rather than emit a policy granting access to "arn:aws:s3:::/*".
        FluentActions.Invoking(() => S3SetupPolicy.ForBuckets(locations))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ForBuckets_SkipsBlankEntriesBetweenRealOnes()
    {
        // Splitting a textarea on newlines leaves blanks behind whenever somebody hits enter twice.
        string policy = S3SetupPolicy.ForBuckets(["docs-bucket", "  ", "shared-bucket"]);

        Parse(policy).GetProperty("Statement").GetArrayLength().Should().Be(6);
    }

    // -- The account-wide grant -----------------------------------------------------

    [Fact]
    public void ForAllBuckets_ReadsEverythingAndChangesNothing()
    {
        // The point of the wide grant is that it is wide in one direction only. A leaked key that
        // can read every bucket is a real cost; one that can also delete them is a different
        // product, so the absence of write actions is the assertion holding the trade together.
        var actions = Parse(S3SetupPolicy.ForAllBuckets()).GetProperty("Statement").EnumerateArray()
            .SelectMany(x => x.GetProperty("Action").EnumerateArray())
            .Select(a => a.GetString()!)
            .ToList();

        actions.Should().BeEquivalentTo(
            ["s3:ListAllMyBuckets", "s3:ListBucket", "s3:GetBucketLocation", "s3:GetObject"]);

        actions.Should().OnlyContain(a =>
            a.StartsWith("s3:Get") || a.StartsWith("s3:List"));
    }

    [Fact]
    public void ForAllBuckets_KeepsListAllMyBucketsOnItsOwnUnscopedStatement()
    {
        // IAM accepts only "*" as the resource for ListAllMyBuckets. Written against
        // arn:aws:s3:::* the statement parses, attaches, and then denies the call it exists for.
        Statement(S3SetupPolicy.ForAllBuckets(), "ConnapseFindBuckets")
            .GetProperty("Resource").GetString().Should().Be("*");
    }

    [Fact]
    public void ForAllBuckets_SeparatesBucketResourcesFromObjectResources()
    {
        string policy = S3SetupPolicy.ForAllBuckets();

        Statement(policy, "ConnapseInspectBuckets").GetProperty("Resource").GetString()
            .Should().Be("arn:aws:s3:::*");

        // Without the trailing /* the object statement names buckets, not keys, and every read is
        // denied -- the same mistake the per-bucket policy is split to avoid.
        Statement(policy, "ConnapseReadObjects").GetProperty("Resource").GetString()
            .Should().Be("arn:aws:s3:::*/*");
    }

    [Theory]
    [InlineData("acme-docs-*", "arn:aws:s3:::acme-docs-*")]
    [InlineData("  acme-*  ", "arn:aws:s3:::acme-*")]
    [InlineData("acme/docs-*", "arn:aws:s3:::acmedocs-*")]
    [InlineData("", "arn:aws:s3:::*")]
    [InlineData(null, "arn:aws:s3:::*")]
    public void ForBucketPattern_ScopesTheBucketStatement(string? pattern, string expected)
    {
        // A slash is dropped rather than kept: it would turn a bucket pattern into a key pattern,
        // which the bucket-level resource cannot match, and the grant would silently allow nothing.
        Statement(S3SetupPolicy.ForBucketPattern(pattern), "ConnapseInspectBuckets")
            .GetProperty("Resource").GetString().Should().Be(expected);
    }

    // -- Choosing a scope -----------------------------------------------------------

    [Fact]
    public void Grant_DefaultsToEveryBucket()
    {
        var grant = S3SetupPolicy.Grant(S3AccessScope.AllBuckets, null);

        grant.CanDiscoverBuckets.Should().BeTrue();
        grant.Summary.Should().Contain("every bucket");
        Parse(grant.Policy).GetProperty("Statement").GetArrayLength().Should().Be(3);
    }

    [Theory]
    [InlineData(S3AccessScope.NamePattern)]
    [InlineData(S3AccessScope.OneBucket)]
    public void Grant_WithoutTheTextItNeeds_FallsBackToTheWidestScope(S3AccessScope scope)
    {
        // The page renders while somebody is still typing. Returning the widest grant keeps a
        // working script on screen; it is safe because the summary beside it says what it allows,
        // so nobody creates a wider credential than the one they read about.
        S3SetupPolicy.Grant(scope, "  ").Policy.Should().Be(S3SetupPolicy.ForAllBuckets());
    }

    [Fact]
    public void Grant_ForOneBucket_CannotDiscoverBuckets()
    {
        // Not a detail: it decides whether the connection form can offer a list of buckets or has
        // to ask someone to type a name exactly right.
        var grant = S3SetupPolicy.Grant(S3AccessScope.OneBucket, "my-bucket", "docs");

        grant.CanDiscoverBuckets.Should().BeFalse();
        grant.Summary.Should().Contain("docs/").And.Contain("my-bucket");
        grant.Policy.Should().NotContain("ListAllMyBuckets");
    }

    [Fact]
    public void Grant_ForAPattern_NamesTheNormalisedPatternInItsSummary()
    {
        // What the summary claims and what the policy grants have to be the same string, or the
        // sentence someone reads before running the script describes a different credential.
        var grant = S3SetupPolicy.Grant(S3AccessScope.NamePattern, " acme/docs-* ");

        grant.Summary.Should().Contain("acmedocs-*");
        grant.Policy.Should().Contain("arn:aws:s3:::acmedocs-*");
    }
}
