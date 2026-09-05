using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Utilities;

[Trait("Category", "Unit")]
public class AwsRolePolicyUpdateTests
{
    private const string RoleArn = "arn:aws:iam::086015909943:role/connapse-ra-bce2666b59d80ef9";

    [Fact]
    public void GenerateCommand_NamesTheRoleAndTheConnapseReadPolicy()
    {
        string cmd = AwsRolePolicyUpdate.GenerateCommand(RoleArn)!;

        cmd.Should().StartWith("aws iam put-role-policy");
        cmd.Should().Contain("--role-name connapse-ra-bce2666b59d80ef9");
        cmd.Should().Contain("--policy-name ConnapseRead");
    }

    [Fact]
    public void GenerateCommand_SubstitutesTheAccountAndCarriesTheCurrentActions()
    {
        string cmd = AwsRolePolicyUpdate.GenerateCommand(RoleArn)!;

        cmd.Should().Contain("086015909943");
        cmd.Should().NotContain(S3SetupPolicy.AccountPlaceholder);
        // The whole point: an old role is brought to the current policy -- here that also strips
        // the grant-writing actions a role minted on an earlier build carried.
        cmd.Should().Contain("s3:ListAccessGrants");
        cmd.Should().NotContain("s3:CreateAccessGrant");
        cmd.Should().NotContain("s3:DeleteAccessGrant");
    }

    [Fact]
    public void GenerateCommand_IsOneLine_SoItPastesIntoAnyShell()
    {
        // A multi-line policy inside single quotes breaks in PowerShell; the command must be one line.
        AwsRolePolicyUpdate.GenerateCommand(RoleArn).Should().NotContain("\n").And.NotContain("\r");
    }

    [Fact]
    public void GenerateCommand_RoleArnWithAPath_TakesTheLastSegmentAsTheName()
    {
        string cmd = AwsRolePolicyUpdate.GenerateCommand(
            "arn:aws:iam::086015909943:role/team/connapse-ra-x")!;

        cmd.Should().Contain("--role-name connapse-ra-x");
        cmd.Should().NotContain("team/");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-arn")]
    [InlineData("arn:aws:s3:::some-bucket")]
    [InlineData("arn:aws:iam::notanaccount:role/x")]
    // Role name with shell metacharacters / spaces is not a valid IAM name and must never reach
    // the generated command (it is interpolated unquoted).
    [InlineData("arn:aws:iam::086015909943:role/x; Start-Process calc; #")]
    [InlineData("arn:aws:iam::086015909943:role/has space")]
    [InlineData("arn:aws:iam::086015909943:role/x'y")]
    public void GenerateCommand_MalformedArn_ReturnsNull(string? arn)
    {
        AwsRolePolicyUpdate.GenerateCommand(arn).Should().BeNull();
    }

    [Fact]
    public void PolicyDocument_SubstitutesTheAccount()
    {
        string policy = AwsRolePolicyUpdate.PolicyDocument("086015909943");

        policy.Should().Contain("086015909943");
        policy.Should().NotContain(S3SetupPolicy.AccountPlaceholder);
    }

    [Fact]
    public void PolicyDocument_NoAccount_KeepsThePlaceholderToSubstitute()
    {
        AwsRolePolicyUpdate.PolicyDocument(null)
            .Should().Contain(S3SetupPolicy.AccountPlaceholder);
    }
}
