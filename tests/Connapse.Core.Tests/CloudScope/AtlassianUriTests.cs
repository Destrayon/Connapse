using Connapse.Storage.CloudScope;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.CloudScope;

[Trait("Category", "Unit")]
public sealed class AtlassianUriTests
{
    private const string Cloud = "11111111-2222-3333-4444-55555555abcd";

    [Fact]
    public void ForPage_And_ForAttachment_BuildTheDocumentedShapes()
    {
        AtlassianUri.ForPage(Cloud, "42").Should().Be($"atlassian://{Cloud}/confluence/page/42");
        AtlassianUri.ForAttachment(Cloud, "42", "7").Should().Be($"atlassian://{Cloud}/confluence/page/42/attachment/7");
    }

    [Theory]
    [InlineData("atlassian://" + Cloud + "/confluence/page/42")]
    [InlineData("atlassian://" + Cloud + "/confluence/page/42/attachment/7")]
    [InlineData("atlassian://11111111-2222-3333-4444-55555555ABCD/confluence/page/42")]
    public void TryParse_ValidUri_ReturnsLowercaseCloudIdAndPageId(string uri)
    {
        AtlassianUri.TryParse(uri, out string cloudId, out string contentId).Should().BeTrue();

        cloudId.Should().Be(Cloud);
        contentId.Should().Be("42");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("s3://bucket/x")]
    [InlineData("atlassian://acme/confluence/page/42")]
    [InlineData("atlassian://" + Cloud + "/confluence/page/")]
    [InlineData("atlassian://" + Cloud + "/confluence/page/4a")]
    [InlineData("atlassian://" + Cloud + "/confluence/page/42/")]
    [InlineData("atlassian://" + Cloud + "/confluence/page/42/extra")]
    [InlineData("atlassian://" + Cloud + "/confluence/page/42/attachment/x")]
    [InlineData("atlassian://" + Cloud + "/confluence/page/42?x=1")]
    [InlineData("atlassian://" + Cloud + "/confluence/page/42#frag")]
    [InlineData("atlassian://" + Cloud + "/confluence/page/42\n")]
    [InlineData("atlassian://" + Cloud + "/confluence/page/../42")]
    [InlineData("atlassian://" + Cloud + "/confluence/blogpost/42")]
    [InlineData("ATLASSIAN://" + Cloud + "/confluence/page/42")]
    public void TryParse_AnythingElse_IsRejected(string? uri) =>
        AtlassianUri.TryParse(uri, out _, out _).Should().BeFalse();
}
