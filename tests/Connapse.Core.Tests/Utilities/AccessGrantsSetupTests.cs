using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Utilities;

/// <summary>
/// The template and command that set up the S3 Access Grants side of per-user permissions.
/// </summary>
[Trait("Category", "Unit")]
public class AccessGrantsSetupTests
{
    [Fact]
    public void GenerateTemplate_CreatesTheThreeResourcesAndNothingElse()
    {
        // An instance for grants to live in, a location declaring what they may govern, and the
        // role that location requires. Anything else appearing here is worth noticing.
        string template = AccessGrantsSetup.GenerateTemplate();

        template.Should().StartWith("AWSTemplateFormatVersion");
        template.Should().Contain("AWS::S3::AccessGrantsInstance");
        template.Should().Contain("AWS::S3::AccessGrantsLocation");
        template.Should().Contain("AWS::IAM::Role");

        template.Should().NotContain("AWS::Cognito", "sign-in goes straight to Identity Center");
        template.Should().NotContain("AWS::SSO::Application", "the token exchange is gone");
    }

    [Fact]
    public void GenerateTemplate_CreatesNoAccessGrant()
    {
        // The standing constraint of the whole feature. A grant names who may read what, and that
        // is the administrator's decision, authored in AWS, which Connapse reads and never writes.
        AccessGrantsSetup.GenerateTemplate()
            .Should().NotContain("AWS::S3::AccessGrant\n")
            .And.NotContain("AccessGrantsInstanceGrant");
    }

    [Fact]
    public void GenerateScript_CreatesNoAccessGrant()
    {
        AccessGrantsSetup.GenerateScript("us-west-1")
            .Should().NotContain("create-access-grant");
    }

    [Fact]
    public void GenerateScript_PinsTheRegionWithNoFallback()
    {
        // Falling back to CloudShell's own region is the exact mistake the discovery step exists to
        // prevent: Identity Center lives in one region, the console opens wherever it was last
        // used, and deploying to the wrong one reads as having no instance at all.
        string script = AccessGrantsSetup.GenerateScript("us-west-1");

        script.Should().Contain("REGION=\"us-west-1\"");
        script.Should().NotContain("aws configure get region");
        script.Should().NotContain("AWS_REGION:-");
    }

    [Fact]
    public void GenerateScript_SurvivesBeingPastedIntoAnInteractiveShell()
    {
        // Two hard-won rules, both learned by disconnecting a live CloudShell session. `set -e` and
        // a bare `exit` terminate the *session* rather than a script when the lines are pasted into
        // an interactive shell, and a line continuation puts that shell into continuation mode for
        // the rest of the paste.
        string script = AccessGrantsSetup.GenerateScript("us-west-1");

        script.Should().NotContain("set -e");
        script.Split('\n').Should().NotContain(l => l.TrimEnd().EndsWith('\\'));
        script.Split('\n').Should().NotContain(l => l.Trim() == "exit 1");
    }

    [Fact]
    public void GenerateScript_ReportsAFailedDeployRatherThanCarryingOn()
    {
        // The regression that removing `set -e` first introduced: the deploy's exit code went
        // unread, so a rollback carried on to report success.
        string script = AccessGrantsSetup.GenerateScript("us-west-1");

        script.Should().Contain("The stack did not deploy.");
        script.Should().Contain("Something above failed.");
    }

    [Fact]
    public void GenerateScript_WithNoRegion_SaysSoRatherThanGuessing()
    {
        string script = AccessGrantsSetup.GenerateScript(null);

        script.Should().Contain("REGION=\"\"");
        script.Should().Contain("Locate your Identity Center instance first");
    }

    [Theory]
    [InlineData("us-west-1", "us-west-1")]
    [InlineData("  eu-central-1  ", "eu-central-1")]
    [InlineData(null, "")]
    [InlineData("", "")]
    // The region arrives from a block the administrator pasted, and lands inside a double-quoted
    // assignment. A quote in it would close that string early and hand the rest of the line to the
    // shell as something else — so the allowlist rejects rather than escapes, and the script falls
    // to the branch that already knows what to say about a missing region.
    [InlineData("us-east-1\"; rm -rf /", "")]
    [InlineData("$(id)", "")]
    [InlineData("US-EAST-1", "")]
    public void SanitiseRegion_KeepsOnlyWhatCouldBeARegion(string? given, string expected)
    {
        AccessGrantsSetup.SanitiseRegion(given).Should().Be(expected);
    }

    [Fact]
    public void SanitiseRegion_RejectsAPastedParagraph()
    {
        // Not a region, however much of it looks like one. Bounded so a whole terminal buffer
        // cannot arrive here as a single very long value.
        AccessGrantsSetup.SanitiseRegion(new string('a', 33)).Should().BeEmpty();
    }
}
