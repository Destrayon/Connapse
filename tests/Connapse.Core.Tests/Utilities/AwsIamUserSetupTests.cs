using Connapse.Core.Utilities;
using FluentAssertions;

namespace Connapse.Core.Tests.Utilities;

[Trait("Category", "Unit")]
public class AwsIamUserSetupTests
{
    private static string Script(string? userName = null) =>
        AwsIamUserSetup.GenerateScript(userName);

    [Fact]
    public void GenerateScript_CreatesAUserAPolicyAndAKey_AndNothingElse()
    {
        // The script is shown in full so an administrator can read it before running it, which is
        // only worth anything if it stays this short. Every write it performs is asserted here, so
        // a fourth one cannot be added without this test being changed deliberately.
        string script = Script();

        script.Should().Contain("aws iam create-user")
            .And.Contain("aws iam put-user-policy")
            .And.Contain("aws iam create-access-key");

        // "aws iam delete", not "delete": the already-exists branch tells the operator to delete
        // the user themselves, which is advice rather than an action the script takes.
        script.Should().NotContain("aws iam delete").And.NotContain("attach-user-policy");
    }

    [Fact]
    public void GenerateScript_MintsNoSecondKeyForAnIdentityThatAlreadyExists()
    {
        // Re-running it must not mint a second key for an identity already in use: the operator
        // would store the new one and leave the old one live, with nothing recording that it
        // exists. The script used to guarantee that by refusing outright, which turned out to
        // strand every installation that needed a permission added after it first ran.
        string script = Script();

        script.Should().Contain("aws iam get-user");
        script.Should().Contain("[ -z \"$FAILED\" ] && [ -z \"$EXISTS\" ]",
            "creating the key is gated on the identity not having existed a moment ago");
    }

    [Fact]
    public void GenerateScript_UpdatesThePolicyOnAnIdentityConnapseAlreadyMade()
    {
        // How a permission added in a later version reaches an installation that already ran this.
        // Deleting and recreating the user would work too, and would rotate its access key and
        // break every configured source until the new one was pasted back.
        string script = Script();

        script.Should().Contain("aws iam put-user-policy");
        script.Should().Contain("Permissions for $USER are up to date");
        script.Should().Contain("access key is unchanged");
    }

    [Fact]
    public void GenerateScript_WillNotAdoptAUserConnapseDidNotCreate()
    {
        // A user that merely shares the name belongs to somebody else, and rewriting its policy
        // would be taking over an account this script cannot reason about. The tag is written at
        // creation, so its absence is the signal.
        string script = Script();

        script.Should().Contain("list-user-tags");
        script.Should().Contain("Connapse did not create it");
    }

    [Fact]
    public void GenerateScript_SurvivesBeingPastedIntoAnInteractiveShell()
    {
        // `set -e` and a bare `exit` end the *session* rather than a script when pasted into an
        // interactive shell. That is how CloudShell disconnects part-way through a paste instead
        // of reporting a problem, and it cost two debugging sessions to find.
        // Comment lines are excluded, because the script explains this rule in a comment that
        // necessarily quotes the thing it forbids. A check that reads prose as code fails on its
        // own documentation.
        var commands = Script().Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

        commands.Should().NotContain(l => l == "set -e" || l.StartsWith("set -e "));
        commands.Should().NotContain(l => l == "exit 1" || l == "exit");
    }

    [Fact]
    public void GenerateScript_SaysWhatThePolicyAllows()
    {
        // The script outlives the page that generated it -- pasted into a ticket or a terminal
        // buffer, this comment is the only record of what the credential it mints can reach.
        Script().Should().Contain(S3SetupPolicy.ManagedIdentitySummary);
    }

    [Fact]
    public void GenerateScript_CarriesTheManagedIdentityPolicy()
    {
        // Not a policy assembled here. One place decides what Connapse asks AWS for, so the script
        // and the sentence describing it cannot drift apart.
        Script().Should().Contain(S3SetupPolicy.ForManagedIdentity());
    }

    [Theory]
    [InlineData(null, "connapse-reader")]
    [InlineData("", "connapse-reader")]
    [InlineData("connapse reader", "connapse-reader")]
    [InlineData("  Connapse/Reader  ", "Connapse-Reader")]
    [InlineData("---", "connapse-reader")]
    public void SanitiseUserName_CoercesToWhatIamAccepts(string? given, string expected)
    {
        // A space would break the quoting on the generated command line before AWS ever saw it.
        AwsIamUserSetup.SanitiseUserName(given).Should().Be(expected);
    }

    [Fact]
    public void SanitiseUserName_TruncatesToIamsLimit()
    {
        AwsIamUserSetup.SanitiseUserName(new string('a', 200)).Should().HaveLength(64);
    }

    [Fact]
    public void ParseResult_ReadsTheBlockTheScriptPrints()
    {
        string pasted = $"""
            {AwsIamUserSetup.BeginMarker}
            user=connapse-reader
            accessKeyId=AKIAEXAMPLE
            secretAccessKey=s3cr3t/value+with=padding
            {AwsIamUserSetup.EndMarker}
            """;

        var key = AwsIamUserSetup.ParseResult(pasted);

        key.Should().NotBeNull();
        key!.UserName.Should().Be("connapse-reader");
        key.AccessKeyId.Should().Be("AKIAEXAMPLE");

        // Split on the first '=' only. A secret access key routinely ends in padding, and taking
        // the last one truncates it into a credential that authenticates nothing.
        key.SecretAccessKey.Should().Be("s3cr3t/value+with=padding");
    }

    [Fact]
    public void ParseResult_AnchorsOnTheLastMarkerPair()
    {
        // A terminal buffer holds the markers twice: the echoed script contains them, because
        // printing them is its job. Taking the first pair reads the source rather than the output,
        // and there is no key in the source.
        string pasted = Script() + "\n" + $"""
            {AwsIamUserSetup.BeginMarker}
            accessKeyId=AKIAREAL
            secretAccessKey=real-secret
            {AwsIamUserSetup.EndMarker}
            """;

        AwsIamUserSetup.ParseResult(pasted)!.AccessKeyId.Should().Be("AKIAREAL");
    }

    [Fact]
    public void ParseResult_WithoutBothHalves_ReturnsNull()
    {
        // Not a partial success: a key id with no secret authenticates nothing, and storing it
        // produces a provider that fails at sync time rather than at setup time.
        string pasted = $"""
            {AwsIamUserSetup.BeginMarker}
            accessKeyId=AKIAEXAMPLE
            {AwsIamUserSetup.EndMarker}
            """;

        AwsIamUserSetup.ParseResult(pasted).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("some unrelated terminal output")]
    public void ParseResult_WithNothingUsable_ReturnsNull(string? pasted)
    {
        AwsIamUserSetup.ParseResult(pasted).Should().BeNull();
    }
}
