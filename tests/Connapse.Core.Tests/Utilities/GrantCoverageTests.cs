using Connapse.Core.Utilities;
using FluentAssertions;

namespace Connapse.Core.Tests.Utilities;

/// <summary>
/// Which of a connection's allowed locations no access grant reaches.
/// </summary>
[Trait("Category", "Unit")]
public class GrantCoverageTests
{
    [Fact]
    public void Ungranted_WithAGrantOnTheBucket_ReportsNothing()
    {
        GrantCoverage.Ungranted(["reports"], ["s3://reports/*"]).Should().BeEmpty();
    }

    [Fact]
    public void Ungranted_WithNoGrantTouchingTheBucket_ReportsIt()
    {
        // The state that makes a connection sync perfectly and return nothing to anybody.
        GrantCoverage.Ungranted(["reports"], ["s3://other-bucket/*"])
            .Should().ContainSingle().Which.Should().Be("reports");
    }

    [Fact]
    public void Ungranted_WithAGrantOnAPrefixInside_ReportsNothing()
    {
        // Partial coverage is a legitimate arrangement — one team granted their own prefix of a
        // wider connection — and flagging it would train an administrator to ignore this.
        GrantCoverage.Ungranted(["reports"], ["s3://reports/team-a/*"]).Should().BeEmpty();
    }

    [Fact]
    public void Ungranted_WithAGrantAboveTheAllowedPrefix_ReportsNothing()
    {
        GrantCoverage.Ungranted(["reports/team-a"], ["s3://reports/*"]).Should().BeEmpty();
    }

    [Fact]
    public void Ungranted_WithAGrantOnASiblingPrefix_ReportsIt()
    {
        // team-b reaches nothing under team-a, so this connection shows that person nothing.
        GrantCoverage.Ungranted(["reports/team-a"], ["s3://reports/team-b/*"])
            .Should().ContainSingle().Which.Should().Be("reports/team-a");
    }

    [Fact]
    public void Ungranted_DoesNotTreatANameThatMerelyStartsTheSame_AsCovered()
    {
        // A bucket sharing a prefix of its name is somebody else's data. Matching as raw text
        // would report this connection as fine when nobody can read a byte of it.
        GrantCoverage.Ungranted(["logs"], ["s3://logs-archive/*"])
            .Should().ContainSingle().Which.Should().Be("logs");

        GrantCoverage.Ungranted(["logs-archive"], ["s3://logs/*"])
            .Should().ContainSingle().Which.Should().Be("logs-archive");
    }

    [Fact]
    public void Ungranted_ReadsAnObjectGrantAsTouchingItsBucket()
    {
        // An object grant has no trailing star. It is the narrowest thing that still reaches this
        // connection, so it counts here — deciding what a *search* may read is GrantScope's job,
        // and that one keeps the distinction.
        GrantCoverage.Ungranted(["reports"], ["s3://reports/q3.pdf"]).Should().BeEmpty();
    }

    [Fact]
    public void Ungranted_ToleratesTheTrailingSlashFormsAwsReturns()
    {
        GrantCoverage.Ungranted(["reports"], ["s3://reports/"]).Should().BeEmpty();
        GrantCoverage.Ungranted(["reports/"], ["s3://reports/*"]).Should().BeEmpty();
    }

    [Fact]
    public void Ungranted_ReportsEveryUncoveredLocationInOrder()
    {
        var result = GrantCoverage.Ungranted(
            ["alpha", "beta", "gamma"], ["s3://beta/*"]);

        result.Should().Equal("alpha", "gamma");
    }

    [Fact]
    public void Ungranted_WithNoAllowedLocations_ReportsNothing()
    {
        // A connection allowing nothing is a separate problem, and StorageLocationPolicy already
        // has an opinion about it. Reporting it here would put two messages on one mistake.
        GrantCoverage.Ungranted([], ["s3://reports/*"]).Should().BeEmpty();
        GrantCoverage.Ungranted(null, ["s3://reports/*"]).Should().BeEmpty();
    }

    [Fact]
    public void Ungranted_WithNoGrantsAtAll_ReportsEveryLocation()
    {
        // The state a fresh Access Grants instance is in, and the one worth saying out loud.
        GrantCoverage.Ungranted(["alpha", "beta"], []).Should().Equal("alpha", "beta");
        GrantCoverage.Ungranted(["alpha"], null).Should().Equal("alpha");
    }

    [Fact]
    public void Ungranted_IgnoresBlankEntries()
    {
        GrantCoverage.Ungranted(["  ", "", "reports"], ["s3://reports/*"]).Should().BeEmpty();
    }
}
