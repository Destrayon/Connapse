using Connapse.Core.Utilities;
using FluentAssertions;

namespace Connapse.Core.Tests.Utilities;

/// <summary>
/// The command a connection prints to grant its buckets to a group.
/// </summary>
[Trait("Category", "Unit")]
public class AccessGrantScriptTests
{
    private const string Group = "69f9f9de-00f1-7088-80ba-6fe7914cb986";

    private static string Script(params string[] locations) =>
        AccessGrantScript.GenerateScript("us-west-1", locations, Group, isGroup: true);

    [Fact]
    public void GenerateScript_LeavesNothingToFillIn()
    {
        // The whole reason this moved onto the connection. The version on the providers page could
        // not know the bucket, so it printed YOUR-BUCKET and GROUP-ID -- and was run with the
        // bucket placeholder still in it, which AWS rejected as an invalid grant.
        string script = Script("test-bucket-for-connapse");

        script.Should().NotContain("YOUR-BUCKET").And.NotContain("GROUP-ID");
        script.Should().Contain("test-bucket-for-connapse/*");
        script.Should().Contain(Group);
    }

    [Fact]
    public void GenerateScript_MakesOneGrantPerBucket()
    {
        // AWS refuses a grant on the bare s3:// location, which would reach every bucket in the
        // region, so several buckets genuinely means several grants.
        string script = Script("a-bucket", "b-bucket/team");

        script.Should().Contain("'a-bucket/*'").And.Contain("'b-bucket/team/*'");
    }

    [Fact]
    public void GenerateScript_DiscoversTheLocationRatherThanAssumingDefault()
    {
        // "default" is what AWS calls the s3:// location and is usually right, but a location
        // registered against one bucket has a generated id. Naming the wrong one fails as
        // InvalidAccessGrant, which reads as a bad grant rather than a bad location.
        string script = Script("reports");

        script.Should().Contain("list-access-grants-locations");
        script.Should().NotContain("--access-grants-location-id default");
    }

    [Fact]
    public void GenerateScript_SkipsAScopeThatIsAlreadyGranted()
    {
        // Running it twice is the normal way to check it worked, and AWS documents no error for
        // creating a grant that already exists -- so a second run might conflict, or might quietly
        // make a duplicate. Reading what exists first makes the outcome the same either way.
        string script = Script("reports");

        script.Should().Contain("list-access-grants ");
        script.Should().Contain("Already granted on");
        script.Should().Contain("continue", "an existing scope is skipped rather than recreated");
    }

    [Fact]
    public void GenerateScript_ComparesScopesAsWholeWordsNotPatterns()
    {
        // A grant scope ends in a star. Matched with a case pattern it would behave as a wildcard
        // and report buckets nobody granted as already covered.
        Script("reports").Should().Contain("[ \"$SCOPE\" = \"s3://$SUBPREFIX\" ]");
    }

    [Fact]
    public void GenerateScript_WithNoGroupChosen_SaysSoRatherThanGrantingToNobody()
    {
        string script = AccessGrantScript.GenerateScript("us-west-1", ["reports"], null, isGroup: true);

        script.Should().Contain("GRANTEE=\"\"");
        script.Should().Contain("No group chosen in Connapse");
    }

    [Fact]
    public void GenerateScript_NamesTheGranteeTypeItWasAskedFor()
    {
        Script("reports").Should().Contain("DIRECTORY_GROUP");

        AccessGrantScript.GenerateScript("us-west-1", ["reports"], Group, isGroup: false)
            .Should().Contain("DIRECTORY_USER");
    }

    [Fact]
    public void GenerateScript_SurvivesBeingPastedIntoAnInteractiveShell()
    {
        var commands = Script("reports").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

        commands.Should().NotContain(l => l == "set -e" || l.StartsWith("set -e "));
        commands.Should().NotContain(l => l == "exit 1" || l == "exit");
    }

    [Theory]
    [InlineData("reports", "reports")]
    [InlineData("reports/team/", "reports/team")]
    [InlineData("  reports  ", "reports")]
    [InlineData(null, "")]
    [InlineData("", "")]
    // Would break out of the single-quoted word it is placed in.
    [InlineData("reports'; rm -rf /", "")]
    [InlineData("$(id)", "")]
    public void SanitiseLocation_KeepsOnlyASafeBucketOrPrefix(string? given, string expected)
    {
        AccessGrantScript.SanitiseLocation(given).Should().Be(expected);
    }

    [Fact]
    public void SanitiseLocation_DropsATrailingSlashSoTheStarDoesNotDouble()
    {
        // "bucket//*" is stored literally by AWS, giving a grant scope that matches nothing anyone
        // will ever ask for — a grant that exists and does nothing.
        Script("reports/team/").Should().Contain("'reports/team/*'")
            .And.NotContain("//*");
    }
}
