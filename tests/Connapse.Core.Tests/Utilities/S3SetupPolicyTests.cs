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

    // -- The identity Connapse creates for itself ----------------------------------

    [Fact]
    public void ForManagedIdentity_IsValidJsonWithAPolicyVersion()
    {
        // IAM rejects a malformed document and the UI presents this as something to paste, so
        // parsing is the assertion that matters -- a string check passes on output nobody can use.
        var root = Parse(S3SetupPolicy.ForManagedIdentity());

        root.GetProperty("Version").GetString().Should().Be("2012-10-17");
        root.GetProperty("Statement").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public void ForManagedIdentity_ReadsEverythingAndChangesNothing()
    {
        // The whole trade rests on this. A credential that can read every bucket is a real cost,
        // accepted knowingly; one that can also delete them is a different product. Connapse has no
        // write surface against a source at all -- IConnector does not expose one -- so a policy
        // granting more asks for authority the product cannot use.
        var actions = Parse(S3SetupPolicy.ForManagedIdentity()).GetProperty("Statement")
            .EnumerateArray()
            .SelectMany(x => x.GetProperty("Action").EnumerateArray())
            .Select(a => a.GetString()!)
            .ToList();

        actions.Should().BeEquivalentTo(
            ["s3:ListAllMyBuckets", "s3:ListBucket", "s3:GetBucketLocation", "s3:GetObject"]);

        actions.Should().OnlyContain(a => a.StartsWith("s3:Get") || a.StartsWith("s3:List"));
        actions.Should().NotContain(a => a.Contains("*"));
    }

    [Fact]
    public void ForManagedIdentity_KeepsListAllMyBucketsOnItsOwnUnscopedStatement()
    {
        // IAM accepts only "*" as the resource for ListAllMyBuckets. Written against
        // arn:aws:s3:::* the statement parses, attaches, and then denies the call it exists for --
        // and that call is what lets the connection form list buckets instead of asking for a name.
        Statement(S3SetupPolicy.ForManagedIdentity(), "ConnapseFindBuckets")
            .GetProperty("Resource").GetString().Should().Be("*");
    }

    [Fact]
    public void ForManagedIdentity_SeparatesBucketResourcesFromObjectResources()
    {
        string policy = S3SetupPolicy.ForManagedIdentity();

        Statement(policy, "ConnapseInspectBuckets").GetProperty("Resource").GetString()
            .Should().Be("arn:aws:s3:::*");

        // Without the trailing /* the object statement names buckets, not keys, and every read is
        // denied while the policy looks correct.
        Statement(policy, "ConnapseReadObjects").GetProperty("Resource").GetString()
            .Should().Be("arn:aws:s3:::*/*");
    }

    [Fact]
    public void ForManagedIdentity_LocatesBucketsWithoutAPrefixCondition()
    {
        // S3Discovery asks for a bucket's region before its first read. GetBucketLocation does not
        // understand s3:prefix, so it belongs on an unconditioned bucket-level statement -- the
        // narrow ForBuckets policies split it out for the same reason.
        var inspect = Statement(S3SetupPolicy.ForManagedIdentity(), "ConnapseInspectBuckets");

        inspect.GetProperty("Action").EnumerateArray().Select(a => a.GetString())
            .Should().Contain("s3:GetBucketLocation");
        inspect.TryGetProperty("Condition", out _).Should().BeFalse();
    }

    [Fact]
    public void ManagedIdentitySummary_SaysBothWhatItReadsAndWhatItCannotDo()
    {
        // This sentence is what an operator reads before running a script that mints a credential.
        // "Every bucket" without "cannot write" describes something scarier than what is granted.
        S3SetupPolicy.ManagedIdentitySummary.Should()
            .Contain("every S3 bucket").And.Contain("cannot write");
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
    public void ForBuckets_PrefixWithoutTrailingSlash_DoesNotLeakIntoASiblingFolder()
    {
        string policy = S3SetupPolicy.ForBuckets(["my-bucket/docs"]);

        Statement(policy, "ConnapseReadObjects0").GetProperty("Resource").GetString()
            .Should().NotBe("arn:aws:s3:::my-bucket/docs*",
                "that also matches docs-archive/, which the operator did not grant");
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

}
