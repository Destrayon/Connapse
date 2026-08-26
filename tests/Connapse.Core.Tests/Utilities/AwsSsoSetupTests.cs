using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Utilities;

/// <summary>
/// The guided AWS identity provider setup: the script an administrator runs in CloudShell, and
/// the block it prints back.
/// </summary>
/// <remarks>
/// Two halves worth different kinds of test. The parser is ordinary code and is tested directly.
/// The script is text this assembly cannot execute, so what is asserted about it is the small set
/// of shell traps that have actually cost something here before — chiefly <c>printf</c> and its
/// leading dash.
/// </remarks>
[Trait("Category", "Unit")]
public class AwsSsoSetupTests
{
    private static string Block(params string[] lines) =>
        AwsSsoSetup.BeginMarker + "\n" + string.Join("\n", lines) + "\n" + AwsSsoSetup.EndMarker;

    // ── The script ────────────────────────────────────────────────────────────────

    [Fact]
    public void GenerateScript_NeverPassesAMarkerAsAPrintfFormatString()
    {
        // Both markers start with dashes, and printf reads a leading '-' as an option. In the
        // SFTP script the end marker as a format string made bash print "invalid option" instead
        // of the marker, so the block came back unterminated and would not parse. Asserting that
        // the marker merely *appears* in the script does not catch this: it appeared, as the
        // thing that failed to print.
        string script = AwsSsoSetup.GenerateScript();

        foreach (string line in script.Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("printf ", StringComparison.Ordinal)) continue;

            string format = trimmed["printf ".Length..].TrimStart();
            format = format.StartsWith('\'') ? format[1..] : format;

            format.Should().NotStartWith("-",
                $"printf would read this as an option, not text: {trimmed}");
        }
    }

    [Fact]
    public void GenerateScript_EmitsBothMarkersThroughAStringPlaceholder()
    {
        // The positive half of the rule above: the markers are arguments, which is what makes
        // them survive their own leading dashes.
        string script = AwsSsoSetup.GenerateScript();

        script.Should().Contain($"'{AwsSsoSetup.BeginMarker}'");
        script.Should().Contain($"'{AwsSsoSetup.EndMarker}'");
    }

    [Fact]
    public void GenerateScript_OnlyReads()
    {
        // The administrator is asked to paste this into a shell holding their console session's
        // credentials. Anything that creates or changes an AWS resource does not belong in it,
        // and the claim that it is read-only is made to them in the UI.
        string script = AwsSsoSetup.GenerateScript();

        foreach (string verb in new[] { "create-", "delete-", "put-", "update-", "attach-", "tag-" })
        {
            script.Should().NotContain($"aws sso-admin {verb}", $"the script must not {verb.TrimEnd('-')}");
        }

        script.Should().Contain("aws sso-admin list-instances");
    }

    [Fact]
    public void GenerateScript_ScansTheSessionRegionBeforeAnyOther()
    {
        // For most people the CloudShell session is already in the right region, and a scan that
        // ignored it would make twenty calls to learn what the first one knew.
        string script = AwsSsoSetup.GenerateScript();

        int home = script.IndexOf("AWS_REGION", StringComparison.Ordinal);
        int loop = script.IndexOf("for R in", StringComparison.Ordinal);

        home.Should().BePositive();
        loop.Should().BeGreaterThan(home);
    }

    [Fact]
    public void CandidateRegions_ExcludeThePartitionsThisScriptCannotServe()
    {
        // GovCloud and the China partitions need different endpoints and a different ARN
        // partition. Including them would produce confident-looking failures rather than a
        // clear "this script is not for you".
        AwsSsoSetup.CandidateRegions.Should().NotContain(r => r.StartsWith("us-gov", StringComparison.Ordinal));
        AwsSsoSetup.CandidateRegions.Should().NotContain(r => r.StartsWith("cn-", StringComparison.Ordinal));
        AwsSsoSetup.CandidateRegions.Should().OnlyHaveUniqueItems();
    }

    // ── Parsing ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseResult_OneInstance_ReadsEveryField()
    {
        var result = AwsSsoSetup.ParseResult(Block(
            "region=eu-west-1",
            "instanceArn=arn:aws:sso:::instance/ssoins-6987abc1234def56",
            "identityStoreId=d-9067890abc",
            "portalUrl=https://d-9067890abc.awsapps.com/start"));

        result.Should().NotBeNull();
        result!.Instances.Should().ContainSingle();
        result.Instances[0].Region.Should().Be("eu-west-1");
        result.Instances[0].InstanceArn.Should().Be("arn:aws:sso:::instance/ssoins-6987abc1234def56");
        result.Instances[0].IdentityStoreId.Should().Be("d-9067890abc");
        result.Instances[0].PortalUrl.Should().Be("https://d-9067890abc.awsapps.com/start");
        result.MissingPermissions.Should().BeEmpty();
    }

    [Fact]
    public void ParseResult_SeveralInstances_KeepsThemSeparate()
    {
        // Multi-region replication gives one portal per region. Merging them would silently
        // pick one, and Connapse cannot know which their users sign in through.
        var result = AwsSsoSetup.ParseResult(Block(
            "region=us-east-1",
            "instanceArn=arn:aws:sso:::instance/ssoins-aaa",
            "identityStoreId=d-aaaaaaaaa1",
            "portalUrl=https://d-aaaaaaaaa1.awsapps.com/start",
            "region=eu-west-1",
            "instanceArn=arn:aws:sso:::instance/ssoins-bbb",
            "identityStoreId=d-bbbbbbbbb2",
            "portalUrl=https://d-bbbbbbbbb2.awsapps.com/start"));

        result!.Instances.Should().HaveCount(2);
        result.Instances.Select(i => i.Region).Should().Equal("us-east-1", "eu-west-1");
        result.Instances.Select(i => i.IdentityStoreId).Should().Equal("d-aaaaaaaaa1", "d-bbbbbbbbb2");
    }

    [Fact]
    public void ParseResult_NoPortalUrl_DerivesItFromTheIdentityStoreId()
    {
        // The default access portal is the identity store id as a subdomain, so a block missing
        // the line is still usable rather than being a dead end.
        var result = AwsSsoSetup.ParseResult(Block(
            "region=us-east-1",
            "instanceArn=arn:aws:sso:::instance/ssoins-aaa",
            "identityStoreId=d-1234567890"));

        result!.Instances.Should().ContainSingle();
        result.Instances[0].PortalUrl.Should().Be("https://d-1234567890.awsapps.com/start");
    }

    [Fact]
    public void ParseResult_Denied_ReportsThePermissionRatherThanLookingEmpty()
    {
        // "Not allowed to look" and "looked, found nothing" are different problems with different
        // advice, and without this they are the same empty list.
        var result = AwsSsoSetup.ParseResult(Block("missingPermission=sso:ListInstances"));

        result.Should().NotBeNull();
        result!.Instances.Should().BeEmpty();
        result.MissingPermissions.Should().Equal("sso:ListInstances");
    }

    [Fact]
    public void ParseResult_FoundNothing_IsAnEmptyResultAndNotAFailureToParse()
    {
        // The scan ran and there was no instance. That is an outcome to report, not a malformed
        // paste — returning null here would tell the administrator they pasted the wrong thing.
        var result = AwsSsoSetup.ParseResult(Block());

        result.Should().NotBeNull();
        result!.Instances.Should().BeEmpty();
        result.MissingPermissions.Should().BeEmpty();
    }

    [Fact]
    public void ParseResult_WholeTerminalBuffer_FindsTheBlockInsideIt()
    {
        // What people actually paste: the prompt, the command echoed back, the progress line,
        // and the block somewhere in the middle.
        string pasted = """
            [cloudshell-user@ip-10-0-0-1 ~]$ bash setup.sh
            Not in this session default region; checking the others. This takes a moment.

            """ + Block(
                "region=ap-southeast-2",
                "instanceArn=arn:aws:sso:::instance/ssoins-xyz",
                "identityStoreId=d-abcdef1234") + """

            Copy the block above, including both marker lines, back into Connapse.
            [cloudshell-user@ip-10-0-0-1 ~]$
            """;

        var result = AwsSsoSetup.ParseResult(pasted);

        result!.Instances.Should().ContainSingle();
        result.Instances[0].Region.Should().Be("ap-southeast-2");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("region=us-east-1\nidentityStoreId=d-1234567890")]
    [InlineData("----- BEGIN CONNAPSE AWS SETUP -----\nregion=us-east-1")]
    public void ParseResult_WithoutBothMarkers_RefusesRatherThanGuessing(string? pasted)
    {
        // Loose key=value lines would happily accept a paste from somewhere else entirely, and
        // the result of that is an identity provider pointed at the wrong account.
        AwsSsoSetup.ParseResult(pasted).Should().BeNull();
    }

    [Fact]
    public void ParseResult_IncompleteRecord_IsDroppedRatherThanHalfBuilt()
    {
        // A region with no instance behind it is not an instance. Admitting it would produce an
        // entry whose ARN and store id are empty strings.
        var result = AwsSsoSetup.ParseResult(Block(
            "region=us-east-1",
            "region=eu-west-1",
            "instanceArn=arn:aws:sso:::instance/ssoins-bbb",
            "identityStoreId=d-bbbbbbbbb2"));

        result!.Instances.Should().ContainSingle();
        result.Instances[0].Region.Should().Be("eu-west-1");
    }
}
