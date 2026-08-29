using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Utilities;

/// <summary>
/// Finding the one region an IAM Identity Center instance lives in, and saying something useful when
/// there isn't one.
/// </summary>
[Trait("Category", "Unit")]
public class IdentityCenterSetupTests
{
    private static string Block(params string[] lines) =>
        IdentityCenterSetup.BeginMarker + "\n"
        + string.Join("\n", lines) + "\n"
        + IdentityCenterSetup.EndMarker;

    // --- the script ---

    [Fact]
    public void GenerateScript_OnlyReads()
    {
        // The whole premise of pasting this into a shell that already holds the administrator's
        // credentials is that they can see it cannot change anything.
        string script = IdentityCenterSetup.GenerateScript();

        script.Should().NotContain("create-");
        script.Should().NotContain("put-");
        script.Should().NotContain("delete-");
        script.Should().NotContain("update-");
    }

    [Fact]
    public void GenerateScript_ChecksTheSessionRegionBeforeScanning()
    {
        // For most people the session region is the answer, and a scan of twenty regions is twenty
        // sequential API calls to learn what the first one already said.
        string script = IdentityCenterSetup.GenerateScript();

        script.Should().Contain("HOME_REGION=\"${AWS_REGION:-$AWS_DEFAULT_REGION}\"");
        script.Should().Contain("[ -n \"$HOME_REGION\" ] && probe \"$HOME_REGION\"");
    }

    [Fact]
    public void GenerateScript_StopsScanningOnceRefused()
    {
        // A sso:ListInstances denial is a property of the caller's policy, not of the region, so
        // continuing would be nineteen more calls refused the same way.
        IdentityCenterSetup.GenerateScript()
            .Should().Contain("[ -n \"$DENIED\" ] && break");
    }

    [Fact]
    public void GenerateScript_EntersNoContinuationMode()
    {
        // What disconnected CloudShell when the Cognito script was pasted. A backslash at end of
        // line, a heredoc, or a quoted string spanning lines all leave an interactive shell waiting
        // for more input while the rest of the paste arrives.
        string script = IdentityCenterSetup.GenerateScript();

        script.Should().NotContain("<<EOF");
        script.Split('\n').Should().NotContain(
            l => l.TrimEnd().EndsWith('\\'),
            "a trailing backslash continues the line");
        (script.Count(c => c == '"') % 2).Should().Be(0, "every double quote should be closed");
    }

    [Fact]
    public void CandidateRegions_AreCommercialOnly()
    {
        // GovCloud and the China partitions need different endpoints and a different ARN partition,
        // so someone there needs a different script rather than a longer list.
        IdentityCenterSetup.CandidateRegions.Should().NotContain(r => r.StartsWith("us-gov"));
        IdentityCenterSetup.CandidateRegions.Should().NotContain(r => r.StartsWith("cn-"));
        IdentityCenterSetup.CandidateRegions.Should().Contain("us-west-1");
    }

    // --- the parser ---

    [Fact]
    public void ParseResult_AnInstance_IsRead()
    {
        var result = IdentityCenterSetup.ParseResult(Block(
            "accountType=management",
            "region=us-west-1",
            "instanceArn=arn:aws:sso:::instance/ssoins-1234567890abcdef",
            "identityStoreId=d-996773e796"));

        result.Should().NotBeNull();
        var instance = result!.Instances.Should().ContainSingle().Subject;
        instance.Region.Should().Be("us-west-1");
        instance.InstanceArn.Should().Be("arn:aws:sso:::instance/ssoins-1234567890abcdef");
        instance.IdentityStoreId.Should().Be("d-996773e796");
        result.Posture.Should().Be(AwsAccountPosture.Management);
    }

    [Fact]
    public void ParseResult_TakesTheLastBlock()
    {
        // The script prints both markers, so a pasted terminal buffer holds each twice: once in the
        // echoed source, once in the real output. The first pair is the echo, and its body parses to
        // nothing — which would report no instance to someone who has one.
        string echoed = IdentityCenterSetup.BeginMarker + "\n"
            + "printf 'region=%s\\n' \"$REGION\"\n"
            + IdentityCenterSetup.EndMarker;

        string pasted = echoed + "\n" + Block(
            "region=eu-west-1",
            "instanceArn=arn:aws:sso:::instance/ssoins-real",
            "identityStoreId=d-real");

        IdentityCenterSetup.ParseResult(pasted)!
            .Instances.Should().ContainSingle().Which.Region.Should().Be("eu-west-1");
    }

    [Fact]
    public void ParseResult_ScanFoundNothing_IsAnAnswerNotAFailure()
    {
        // The scan ran and there is genuinely no instance. That has to reach the caller as an empty
        // result so the page can offer the right next step, rather than as "you pasted the wrong
        // thing".
        var result = IdentityCenterSetup.ParseResult(Block("accountType=standalone"));

        result.Should().NotBeNull();
        result!.Instances.Should().BeEmpty();
        result.MissingPermissions.Should().BeEmpty();
        result.Posture.Should().Be(AwsAccountPosture.Standalone);
        result.CanEnableItself.Should().BeTrue();
    }

    [Fact]
    public void ParseResult_Refused_IsDistinctFromFindingNothing()
    {
        // Identical in the result otherwise, and they need opposite advice: fix your policy versus
        // enable an instance.
        var result = IdentityCenterSetup.ParseResult(Block(
            "accountType=member",
            "missingPermission=sso:ListInstances"));

        result!.Instances.Should().BeEmpty();
        result.MissingPermissions.Should().ContainSingle().Which.Should().Be("sso:ListInstances");
    }

    [Fact]
    public void ParseResult_MemberAccount_CannotEnableAnInstanceItself()
    {
        // Enabling an instance is an organisation-wide act. No amount of IAM permission on a member
        // account changes that, so telling them to go and do it would waste their time.
        IdentityCenterSetup.ParseResult(Block("accountType=member"))!
            .CanEnableItself.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("some unrelated terminal output")]
    [InlineData("----- BEGIN CONNAPSE IDENTITY CENTER -----\nregion=us-west-1")]
    public void ParseResult_WithoutBothMarkers_IsNull(string pasted)
    {
        // Guessing at loose key=value lines would accept a paste from somewhere else entirely.
        IdentityCenterSetup.ParseResult(pasted).Should().BeNull();
    }

    [Fact]
    public void ParseResult_AnUnknownAccountType_StaysUnknown()
    {
        // A newer script talking to an older Connapse should not have its answer guessed at.
        IdentityCenterSetup.ParseResult(Block("accountType=something-new"))!
            .Posture.Should().Be(AwsAccountPosture.Unknown);
    }

    [Fact]
    public void ParseResult_TwoInstances_AreBothRead()
    {
        // Multi-region replication. Rare, and the administrator will know they have it — but
        // silently dropping the second would be a lie about what the scan saw.
        var result = IdentityCenterSetup.ParseResult(Block(
            "region=us-west-1",
            "instanceArn=arn:aws:sso:::instance/ssoins-a",
            "identityStoreId=d-a",
            "region=eu-west-1",
            "instanceArn=arn:aws:sso:::instance/ssoins-b",
            "identityStoreId=d-b"));

        result!.Instances.Should().HaveCount(2);
        result.Instances.Select(i => i.Region).Should().Equal("us-west-1", "eu-west-1");
    }

    [Fact]
    public void ParseResult_AHalfRecord_IsDropped()
    {
        // A truncated paste, which is common when a terminal wraps. Half an instance is not an
        // instance, and storing one would fail much later with an ARN that names nothing.
        IdentityCenterSetup.ParseResult(Block(
            "region=us-west-1",
            "instanceArn=arn:aws:sso:::instance/ssoins-a"))!
            .Instances.Should().BeEmpty();
    }
}
