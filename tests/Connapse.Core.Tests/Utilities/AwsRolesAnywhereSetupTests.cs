using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Utilities;

[Trait("Category", "Unit")]
public class AwsRolesAnywhereSetupTests
{
    private static string Block(string ta, string profile, string role, string region) =>
        $"""
        {AwsRolesAnywhereSetup.BeginMarker}
        trustAnchorArn={ta}
        profileArn={profile}
        roleArn={role}
        region={region}
        {AwsRolesAnywhereSetup.EndMarker}
        """;

    [Fact]
    public void ParseResult_ReadsAllFourArnsFromTheBlock()
    {
        AwsRolesAnywhereArns? result = AwsRolesAnywhereSetup.ParseResult(Block(
            "arn:aws:rolesanywhere:us-east-1:111:trust-anchor/ta",
            "arn:aws:rolesanywhere:us-east-1:111:profile/pf",
            "arn:aws:iam::111:role/connapse-rolesanywhere",
            "us-east-1"));

        result.Should().Be(new AwsRolesAnywhereArns(
            "arn:aws:rolesanywhere:us-east-1:111:trust-anchor/ta",
            "arn:aws:rolesanywhere:us-east-1:111:profile/pf",
            "arn:aws:iam::111:role/connapse-rolesanywhere",
            "us-east-1"));
    }

    [Fact]
    public void ParseResult_AnchorsOnTheLastMarkerPair()
    {
        string echoedThenReal =
            Block("arn:ta:echoed", "arn:pf:echoed", "arn:role:echoed", "us-west-2")
            + "\n"
            + Block("arn:aws:rolesanywhere:us-east-1:111:trust-anchor/real",
                    "arn:aws:rolesanywhere:us-east-1:111:profile/real",
                    "arn:aws:iam::111:role/real", "us-east-1");

        AwsRolesAnywhereSetup.ParseResult(echoedThenReal)!.TrustAnchorArn
            .Should().Be("arn:aws:rolesanywhere:us-east-1:111:trust-anchor/real");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no markers here")]
    public void ParseResult_WithoutAUsableBlock_ReturnsNull(string? pasted)
    {
        AwsRolesAnywhereSetup.ParseResult(pasted).Should().BeNull();
    }

    [Fact]
    public void ParseResult_MissingARequiredArn_ReturnsNull()
    {
        string block =
            $"{AwsRolesAnywhereSetup.BeginMarker}\ntrustAnchorArn=arn:ta\nroleArn=arn:role\nregion=us-east-1\n{AwsRolesAnywhereSetup.EndMarker}";
        AwsRolesAnywhereSetup.ParseResult(block).Should().BeNull(); // profileArn absent
    }

    private const string SampleCert =
        "-----BEGIN CERTIFICATE-----\nMIIBExampleExampleExample\n-----END CERTIFICATE-----";

    private static string Script() => AwsRolesAnywhereSetup.GenerateScript(SampleCert, "us-east-1");

    [Fact]
    public void GenerateScript_ReusesSharedRoleGuardedByTheConnapseTag()
    {
        string script = Script();
        script.Should().Contain("aws iam get-role --role-name");
        script.Should().Contain("aws iam list-role-tags");
        script.Should().Contain("CreatedBy"); // reuse guard
        script.Should().Contain("aws iam create-role");
        script.Should().Contain("Key=CreatedBy,Value=Connapse"); // tags a newly created role
    }

    [Fact]
    public void GenerateScript_SharedRoleTrustPolicyAcceptsAnyConnapseTrustAnchorInTheAccount()
    {
        string script = Script();
        script.Should().Contain("rolesanywhere.amazonaws.com");
        script.Should().Contain("ArnLike");
        script.Should().Contain("arn:aws:rolesanywhere:*:__CONNAPSE_ACCOUNT_ID__:trust-anchor/*"); // not a pinned ARN
        script.Should().Contain("sts:AssumeRole");
        script.Should().Contain("sts:TagSession");
    }

    [Fact]
    public void GenerateScript_AppliesTheSameConnapseReadPolicyAsTheUserPath()
    {
        string script = Script();
        script.Should().Contain("aws iam put-role-policy");
        script.Should().Contain("--policy-name ConnapseRead");
        script.Should().Contain(S3SetupPolicy.ForManagedIdentity().Replace("\r\n", "\n"));
        script.Should().Contain($"//{S3SetupPolicy.AccountPlaceholder}/$ACCOUNT"); // account substitution
    }

    [Fact]
    public void GenerateScript_ReusesTheSharedProfileThenCreatesAPerInstanceTrustAnchor()
    {
        string script = Script();
        script.Should().Contain("aws rolesanywhere list-profiles");
        script.Should().Contain("aws rolesanywhere create-profile");
        script.Should().Contain("aws rolesanywhere list-trust-anchors");
        script.Should().Contain("aws rolesanywhere create-trust-anchor");
        script.Should().Contain("openssl x509 -noout -fingerprint -sha256"); // per-instance name from the cert
        script.Should().Contain("CERTIFICATE_BUNDLE");
        script.Should().Contain(SampleCert.Replace("\r\n", "\n")); // the cert is embedded
    }

    [Fact]
    public void GenerateScript_PrintsTheArnBlockWithTheFourValues_AndPinsTheRegion()
    {
        string script = Script();
        script.Should().Contain(AwsRolesAnywhereSetup.BeginMarker);
        script.Should().Contain(AwsRolesAnywhereSetup.EndMarker);
        script.Should().Contain("trustAnchorArn=");
        script.Should().Contain("profileArn=");
        script.Should().Contain("roleArn=");
        script.Should().Contain("region=us-east-1");
        script.Should().NotContain("aws configure get region"); // region pinned, no fallback
    }

    [Fact]
    public void GenerateScript_SurvivesAnInteractiveShell()
    {
        string[] lines = Script().Split('\n');
        string[] code = lines.Where(l => !l.TrimStart().StartsWith('#')).ToArray();

        code.Should().NotContain(l => l.Trim() == "set -e");
        code.Should().NotContain(l => l.Trim() == "exit" || l.Trim().StartsWith("exit "));
        code.Should().NotContain(l => l.TrimEnd().EndsWith(" \\")); // no line-continuations
        string.Join('\n', code).Count(c => c == '\'').Should().Match(n => n % 2 == 0); // balanced single quotes
    }

    [Theory]
    [InlineData("us-east-1\"; rm -rf /", "")]
    [InlineData("$(id)", "")]
    [InlineData("us-west-2", "us-west-2")]
    public void GenerateScript_SanitisesTheRegion(string input, string expectedInBlock)
    {
        string script = AwsRolesAnywhereSetup.GenerateScript(SampleCert, input);
        script.Should().Contain($"region={expectedInBlock}".TrimEnd());
        if (expectedInBlock.Length == 0)
            script.Should().NotContain("rm -rf");
    }
}
