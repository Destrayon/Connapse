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
    public void GenerateScript_StopsWhenTheUserAlreadyExists()
    {
        // Re-running it must not mint a second key for an identity already in use: the operator
        // would store the new one and leave the old one live, with nothing recording that it exists.
        string script = Script();

        script.Should().Contain("aws iam get-user").And.Contain("exit 1");
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
