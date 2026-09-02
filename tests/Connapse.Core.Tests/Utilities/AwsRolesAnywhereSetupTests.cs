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

    private const string TrustAnchorArn = "arn:aws:rolesanywhere:us-east-1:111111111111:trust-anchor/ta";
    private const string ProfileArn = "arn:aws:rolesanywhere:us-east-1:111111111111:profile/pf";
    private const string RoleArn = "arn:aws:iam::111111111111:role/connapse-ra-x";
    private const string Region = "us-east-1";

    [Fact]
    public void ParseResult_ReadsAllFourArnsFromTheBlock()
    {
        AwsRolesAnywhereArns? result = AwsRolesAnywhereSetup.ParseResult(Block(
            TrustAnchorArn, ProfileArn, RoleArn, Region));

        result.Should().Be(new AwsRolesAnywhereArns(TrustAnchorArn, ProfileArn, RoleArn, Region));
    }

    [Fact]
    public void ParseResult_AnchorsOnTheLastMarkerPair()
    {
        string echoedThenReal =
            Block("arn:ta:echoed", "arn:pf:echoed", "arn:role:echoed", "us-west-2")
            + "\n"
            + Block(TrustAnchorArn, ProfileArn, RoleArn, Region);

        AwsRolesAnywhereSetup.ParseResult(echoedThenReal)!.TrustAnchorArn
            .Should().Be(TrustAnchorArn);
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

    [Fact]
    public void ParseResult_TrustAnchorArnWithWrongService_ReturnsNull()
    {
        string badTrustAnchor = "arn:aws:iam:us-east-1:111111111111:trust-anchor/ta";
        AwsRolesAnywhereSetup.ParseResult(Block(badTrustAnchor, ProfileArn, RoleArn, Region))
            .Should().BeNull();
    }

    [Fact]
    public void ParseResult_ProfileArnInADifferentRegionThanTheRegionField_ReturnsNull()
    {
        string wrongRegionProfile = "arn:aws:rolesanywhere:us-west-2:111111111111:profile/pf";
        AwsRolesAnywhereSetup.ParseResult(Block(TrustAnchorArn, wrongRegionProfile, RoleArn, Region))
            .Should().BeNull();
    }

    [Theory]
    [InlineData("us-east-1/evil")]
    [InlineData("us-east-1;x")]
    public void ParseResult_RegionFieldWithAnInjectionCharacter_ReturnsNull(string dirtyRegion)
    {
        AwsRolesAnywhereSetup.ParseResult(Block(TrustAnchorArn, ProfileArn, RoleArn, dirtyRegion))
            .Should().BeNull();
    }

    [Fact]
    public void ParseResult_CrossAccountArns_ReturnsNull()
    {
        string otherAccountRole = "arn:aws:iam::222222222222:role/connapse-ra-x";
        AwsRolesAnywhereSetup.ParseResult(Block(TrustAnchorArn, ProfileArn, otherAccountRole, Region))
            .Should().BeNull();
    }

    [Fact]
    public void ParseResult_RoleArnWithANonEmptyRegionSegment_ReturnsNull()
    {
        string regionedRole = "arn:aws:iam:us-east-1:111111111111:role/connapse-ra-x";
        AwsRolesAnywhereSetup.ParseResult(Block(TrustAnchorArn, ProfileArn, regionedRole, Region))
            .Should().BeNull();
    }

    [Fact]
    public void ParseResult_RoleArnWithANonNumericAccount_ReturnsNull()
    {
        string badAccountRole = "arn:aws:iam::not-an-account:role/connapse-ra-x";
        AwsRolesAnywhereSetup.ParseResult(Block(TrustAnchorArn, ProfileArn, badAccountRole, Region))
            .Should().BeNull();
    }

    private const string SampleCaCert =
        "-----BEGIN CERTIFICATE-----\nMIIBExampleCaCertExampleCa\n-----END CERTIFICATE-----";

    private static string Script() => AwsRolesAnywhereSetup.GenerateScript(SampleCaCert, "us-east-1");

    [Fact]
    public void GenerateScript_DerivesAPerInstanceNameFromTheCertFingerprint()
    {
        string script = Script();
        script.Should().Contain("openssl x509 -noout -fingerprint -sha256");
        script.Should().Contain("connapse-ra-");
    }

    [Fact]
    public void GenerateScript_CreatesTheTrustAnchorBeforeTheRole_WithRegionPinned()
    {
        string script = Script();
        script.Should().Contain("aws rolesanywhere create-trust-anchor");
        script.Should().Contain("aws iam create-role");

        int trustAnchorIndex = script.IndexOf("aws rolesanywhere create-trust-anchor", StringComparison.Ordinal);
        int roleIndex = script.IndexOf("aws iam create-role", StringComparison.Ordinal);
        trustAnchorIndex.Should().BeLessThan(roleIndex);

        script.Should().Contain("create-trust-anchor --region \"$REGION\"");
    }

    [Fact]
    public void GenerateScript_RoleTrustPolicyPinsToThisTrustAnchorViaArnEquals()
    {
        string script = Script();
        script.Should().Contain("rolesanywhere.amazonaws.com");
        script.Should().Contain("ArnEquals");
        script.Should().Contain("aws:SourceArn");
        script.Should().Contain("//__TA_ARN__/$TA_ARN");
        script.Should().NotContain("ArnLike");
        script.Should().NotContain("trust-anchor/*");
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
    public void GenerateScript_CreatesAPerInstanceProfileWithRegionPinned()
    {
        string script = Script();
        script.Should().Contain("aws rolesanywhere create-profile");
        script.Should().Contain("create-profile --region \"$REGION\"");
    }

    [Fact]
    public void GenerateScript_UsesMktempNotAFixedSourceFilePath()
    {
        string script = Script();
        script.Should().Contain("mktemp");
        script.Should().NotContain("connapse-ta-source.json");
    }

    [Fact]
    public void GenerateScript_EmbedsTheCaCertificate()
    {
        Script().Should().Contain(SampleCaCert.Replace("\r\n", "\n"));
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
    public void GenerateScript_RequiresARegion_AndFailsCleanlyWithoutOne()
    {
        string script = AwsRolesAnywhereSetup.GenerateScript(SampleCaCert, "$(id)");
        script.Should().Contain("no valid AWS region");
        script.Should().NotContain("rm -rf");
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
        string script = AwsRolesAnywhereSetup.GenerateScript(SampleCaCert, input);
        if (expectedInBlock.Length == 0)
        {
            script.Should().NotContain("rm -rf");
            script.Should().Contain("no valid AWS region");
        }
        else
        {
            script.Should().Contain($"region={expectedInBlock}");
        }
    }
}
