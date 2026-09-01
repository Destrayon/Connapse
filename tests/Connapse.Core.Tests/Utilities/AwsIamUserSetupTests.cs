using Connapse.Core.Utilities;
using FluentAssertions;

namespace Connapse.Core.Tests.Utilities;

[Trait("Category", "Unit")]
public class AwsIamUserSetupTests
{
    private static string Script(string? userName = null) =>
        AwsIamUserSetup.GenerateScript(userName);

    [Fact]
    public void RequiredPermissions_CoversEveryIamReadUsedToUpdateAnExistingUser()
    {
        AwsIamUserSetup.RequiredPermissions.Should().Contain("iam:ListUserTags",
            "the update branch verifies the CreatedBy tag before it changes the user's policy");
    }

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

        // An odd number of quotes leaves the shell waiting for the rest of a string that never
        // arrives, so the paste appears to hang with no error — which is exactly how this shipped
        // once, with a stray quote appended after the final `fi`. Comment lines are excluded
        // because prose legitimately contains an apostrophe.
        commands.Sum(l => l.Count(c => c == '\''))
            .Should().Match(n => n % 2 == 0, "every quote in a command must be closed");
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
        //
        // Compared with the policy's newlines normalised, because the script's are: the whole
        // script is forced to LF so a saved copy runs under a real Linux bash, and the serializer
        // writes whatever this platform calls a newline.
        Script().Should().Contain(S3SetupPolicy.ForManagedIdentity().Replace("\r\n", "\n"));
    }

    [Fact]
    public void GenerateScript_SubstitutesTheAccountIntoThePolicy()
    {
        // The grant-read permission names this account's Access Grants instances rather than every
        // resource, and the account number is not known where the policy is built -- it exists only
        // in the shell session running this. Left unsubstituted the ARN is malformed and IAM refuses
        // the whole document, so the placeholder must never reach AWS.
        string script = Script();

        script.Should().Contain("aws sts get-caller-identity",
            "the account number has to come from somewhere");
        script.Should().Contain($"POLICY=${{POLICY//{S3SetupPolicy.AccountPlaceholder}/$ACCOUNT}}");

        // Both halves present: the policy carries the placeholder, and the script replaces it.
        S3SetupPolicy.ForManagedIdentity().Should().Contain(S3SetupPolicy.AccountPlaceholder);
    }

    [Fact]
    public void GenerateScript_ScopesTheGrantReadButNotTheDirectoryReads()
    {
        // Split deliberately. The Access Grants read is the call that can enumerate what every
        // grantee may see, and AWS documents a resource form for it. The Identity Store reads stay
        // on "*" because the reference does not confirm they accept one, and a wrong ARN there
        // fails as AccessDenied -- which the resolver reads as an outage and turns into an empty
        // result set, with nothing anywhere saying why.
        string policy = S3SetupPolicy.ForManagedIdentity();

        policy.Should().Contain("access-grants/*");
        policy.Should().Contain("ConnapseReadGrants").And.Contain("ConnapseReadDirectory");
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
