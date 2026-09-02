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
}
