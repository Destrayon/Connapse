using Connapse.Core;
using FluentAssertions;

namespace Connapse.Core.Tests.Models;

[Trait("Category", "Unit")]
public class AzblobUriTests
{
    [Theory]
    [InlineData("azblob://acct/docs/a/b/file.txt", "acct", "docs", "a/b/file.txt")]
    [InlineData("azblob://acct/docs/file.txt", "acct", "docs", "file.txt")]
    public void TryParse_WellFormed_ReturnsComponents(string uri, string acct, string fs, string path)
    {
        AzblobUri.TryParse(uri, out Gen2Path p).Should().BeTrue();
        p.Account.Should().Be(acct);
        p.FileSystem.Should().Be(fs);
        p.Path.Should().Be(path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("s3://bucket/key")]
    [InlineData("azblob://acct")]          // no container or path
    [InlineData("azblob://acct/docs")]     // no blob path
    [InlineData("azblob://acct/docs/")]    // empty blob path
    public void TryParse_MalformedOrNonAzblob_ReturnsFalse(string? uri) =>
        AzblobUri.TryParse(uri, out _).Should().BeFalse();

    [Fact]
    public void IsAzblob_MatchesSchemeOnly()
    {
        AzblobUri.IsAzblob("azblob://a/c/x").Should().BeTrue();
        AzblobUri.IsAzblob("s3://b/k").Should().BeFalse();
        AzblobUri.IsAzblob(null).Should().BeFalse();
    }
}
