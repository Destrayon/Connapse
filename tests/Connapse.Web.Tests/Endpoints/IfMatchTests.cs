using Connapse.Web.Endpoints;
using FluentAssertions;
using Microsoft.Extensions.Primitives;

namespace Connapse.Web.Tests.Endpoints;

/// <summary>
/// The If-Match header a settings save sends (RFC 9110 §13.1.1): no header or <c>*</c> saves unconditionally,
/// a list of tags saves only against one of them, a weak tag never matches, and anything else is refused.
/// </summary>
[Trait("Category", "Unit")]
public class IfMatchTests
{
    [Fact]
    public void TryRead_NoHeader_IsUnconditional()
    {
        IfMatch.TryRead(StringValues.Empty, out var versions).Should().BeTrue();
        versions.Should().BeNull();
    }

    [Fact]
    public void TryRead_StarAlone_IsUnconditional()
    {
        IfMatch.TryRead("*", out var versions).Should().BeTrue();
        versions.Should().BeNull();
    }

    [Theory]
    [InlineData("\"abc\"", new[] { "abc" })]
    [InlineData("\"abc\", \"def\"", new[] { "abc", "def" })]
    [InlineData("W/\"abc\", \"def\"", new[] { "def" })]
    [InlineData("W/\"abc\"", new string[0])]
    public void TryRead_Tags_AreTheStrongOnesUnquoted(string header, string[] expected)
    {
        IfMatch.TryRead(header, out var versions).Should().BeTrue();
        versions.Should().Equal(expected);
    }

    [Fact]
    public void TryRead_TagsAcrossHeaderLines_AreAllRead()
    {
        IfMatch.TryRead(new StringValues(["\"abc\"", "\"def\""]), out var versions).Should().BeTrue();
        versions.Should().Equal("abc", "def");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("\"abc")]
    [InlineData("")]
    [InlineData(",")]
    [InlineData("\"stale\", *")]
    [InlineData("*, *")]
    [InlineData("\"abc\", junk")]
    [InlineData("junk, \"abc\"")]
    public void TryRead_NotStarAloneOrAListOfTags_IsRefused(string header) =>
        IfMatch.TryRead(header, out _).Should().BeFalse();

    [Fact]
    public void TryRead_StarOnAnotherHeaderLine_IsRefused() =>
        IfMatch.TryRead(new StringValues(["\"stale\"", "*"]), out _).Should().BeFalse();

    [Fact]
    public void ETag_QuotesTheVersion() =>
        IfMatch.ETag("abc").Should().Be("\"abc\"");

    [Fact]
    public void ETag_ReadsBackAsTheVersion()
    {
        IfMatch.TryRead(IfMatch.ETag("0f"), out var versions).Should().BeTrue();
        versions.Should().Equal("0f");
    }
}
