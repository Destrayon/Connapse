using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Utilities;

/// <summary>
/// The absolute address a connector reports for each file, which a permission filter will match
/// scopes against.
/// </summary>
/// <remarks>
/// The authority carries the weight here. Two connections whose URIs collide would let a rule
/// written for one silently reach the other's documents, and that is a grant nobody wrote.
/// </remarks>
[Trait("Category", "Unit")]
public class ResourceUriTests
{
    [Fact]
    public void ForS3_WithBucketAndKey_NamesBoth()
    {
        ResourceUri.ForS3("acme", "team/docs/q3.pdf")
            .Should().Be("s3://acme/team/docs/q3.pdf");
    }

    [Theory]
    [InlineData("docs/a.md", "s3://acme/docs/a.md")]
    [InlineData("/docs/a.md", "s3://acme//docs/a.md")]
    [InlineData("//docs/a.md", "s3://acme///docs/a.md")]
    public void ForS3_WithSlashesInTheKey_CarriesThemVerbatim(string key, string expected)
    {
        // S3 treats these as three distinct keys. Normalising here would merge documents that are
        // not the same object, which is the exact loss that made reconstructing the URI from the
        // stored path unusable in the first place.
        ResourceUri.ForS3("acme", key).Should().Be(expected);
    }

    [Fact]
    public void Schemes_AreDistinctPerProvider()
    {
        // A resolver reads these back. A shared scheme would make rules from two different
        // providers indistinguishable by prefix, which is how the filter will compare them.
        ResourceUri.ForS3("a", "k").Should().StartWith("s3://");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ForS3_WithoutABucket_Throws(string? bucket)
    {
        // A URI with an empty authority is worse than none: "s3:///key" would prefix-match every
        // rule written against that scheme.
        FluentActions.Invoking(() => ResourceUri.ForS3(bucket!, "k"))
            .Should().Throw<ArgumentException>();
    }

}
