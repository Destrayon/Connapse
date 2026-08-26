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
        root.GetProperty("Statement").GetArrayLength().Should().Be(2);
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

        actions.Should().BeEquivalentTo(S3SetupPolicy.ReadActions);
        actions.Should().NotContain(a => a!.Contains("Put") || a.Contains("Delete") || a.Contains("*"));
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
        root.GetProperty("Statement").GetArrayLength().Should().Be(4, "two statements per bucket");
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
        sids.Should().HaveCount(6);
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

        Parse(policy).GetProperty("Statement").GetArrayLength().Should().Be(4);
    }
}
