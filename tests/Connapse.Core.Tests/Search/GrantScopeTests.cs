using Connapse.Core;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Search;

/// <summary>
/// Turning what AWS reports into something safe to match against a document URI.
/// </summary>
[Trait("Category", "Unit")]
public class GrantScopeTests
{
    [Fact]
    public void Parse_BucketScopeWithoutSlash_GainsOne()
    {
        // The leak this exists to close. AWS writes a whole-bucket grant as "s3://bucket*" with no
        // separating slash, so trimming the asterisk leaves "s3://acme" -- which prefix-matches
        // "s3://acme-secrets/payroll.xlsx" just as happily as "s3://acme/report.pdf".
        var match = GrantScope.Parse("s3://acme*");

        match.Value.Should().Be("s3://acme/");
        match.IsExact.Should().BeFalse();
    }

    [Fact]
    public void Parse_BucketScopeWithSlashStar_IsTheSameThing()
    {
        // The other page documents the identical grant this way. Both must land on one form or
        // the predicate's behaviour depends on which AWS doc the response happened to follow.
        GrantScope.Parse("s3://acme/*").Should().Be(GrantScope.Parse("s3://acme*"));
    }

    [Fact]
    public void Parse_PrefixScope_KeepsThePrefixExactlyAsWritten()
    {
        // "s3://acme/team*" means keys beginning "team" -- including "team-archive/". That is what
        // the administrator wrote, so no slash is added here. Only the bucket-only form is special.
        var match = GrantScope.Parse("s3://acme/team*");

        match.Value.Should().Be("s3://acme/team");
        match.IsExact.Should().BeFalse();
    }

    [Fact]
    public void Parse_PrefixScopeWithNoAsterisk_IsStillAPrefix()
    {
        // AWS's Java example returns the same grant with no trailing asterisk at all.
        var match = GrantScope.Parse("s3://acme/team/");

        match.Value.Should().Be("s3://acme/team/");
        match.IsExact.Should().BeFalse();
    }

    [Fact]
    public void Parse_ObjectScope_MatchesByEqualityNotPrefix()
    {
        // A grant for one object is a grant for one object. As a prefix it would also admit
        // "report.pdf.bak", which is a different object the administrator did not name.
        var match = GrantScope.Parse("s3://acme/reports/q3.pdf", isObjectScope: true);

        match.Value.Should().Be("s3://acme/reports/q3.pdf");
        match.IsExact.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_WithBlank_Throws(string scope)
    {
        FluentActions.Invoking(() => GrantScope.Parse(scope))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_WithNull_Throws()
    {
        FluentActions.Invoking(() => GrantScope.Parse(null!))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_NonS3Scheme_Throws()
    {
        // Rather than silently producing a rule that matches nothing, which would read as a
        // denial and send whoever debugs it looking at permissions instead of at parsing.
        FluentActions.Invoking(() => GrantScope.Parse("azblob://acct/container/"))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_SchemeWithNoBucket_Throws()
    {
        FluentActions.Invoking(() => GrantScope.Parse("s3://*"))
            .Should().Throw<ArgumentException>();
    }
}
