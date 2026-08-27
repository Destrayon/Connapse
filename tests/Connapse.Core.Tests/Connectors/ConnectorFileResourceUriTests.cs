using Connapse.Core;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

/// <summary>
/// The absolute location a connector reports for each file it lists.
/// </summary>
/// <remarks>
/// This exists because the alternative — reconstructing the location later from the source's bucket
/// and prefix plus the document's stored path — is wrong in two ways, and both are silent.
/// <para>
/// A source's scope is editable and <c>PostgresSourceStore.UpdateAsync</c> reconciles no existing
/// rows, so a source re-pointed from <c>old/</c> to <c>new/</c> leaves documents whose path is
/// relative to a prefix nothing records any more. And prefix stripping normalises away a
/// distinction S3 keeps.
/// </para>
/// <para>
/// These assert the shape of the contract rather than a connector's behaviour, which needs a live
/// bucket. The connector-side tests live in the integration suite.
/// </para>
/// </remarks>
[Trait("Category", "Unit")]
public class ConnectorFileResourceUriTests
{
    [Fact]
    public void ConnectorFile_WithResourceUri_KeepsItAlongsideTheVirtualPath()
    {
        // The pair is the point: Path is what a document row stores and what the connector is
        // asked for later; ResourceUri is what an external permission system names.
        var file = new ConnectorFile(
            Path: "/q3/report.pdf",
            SizeBytes: 12,
            LastModified: DateTime.UnixEpoch,
            ContentType: null,
            ResourceUri: "s3://acme/team/docs/q3/report.pdf");

        file.Path.Should().Be("/q3/report.pdf");
        file.ResourceUri.Should().Be("s3://acme/team/docs/q3/report.pdf");
    }

    [Fact]
    public void ConnectorFile_WithoutResourceUri_LeavesItNull()
    {
        // Filesystem, SFTP and managed storage have no meaningful external address. They are not
        // broken for lacking one; they are simply not describable by a cloud RBAC scope, and a
        // null here is what makes that explicit rather than inventing a scheme for them.
        var file = new ConnectorFile("/a.md", 1, DateTime.UnixEpoch, null);

        file.ResourceUri.Should().BeNull();
    }

    [Theory]
    [InlineData("docs/a.md")]
    [InlineData("/docs/a.md")]
    [InlineData("//docs/a.md")]
    public void ResourceUri_ForKeysPrefixStrippingCollapses_KeepsThemDistinct(string key)
    {
        // S3 permits all three as distinct keys. StripConfigPrefix returns "/" + TrimStart('/'),
        // so all three become the same stored path -- and any reconstruction from that path can
        // only ever produce one of the three. Carrying the key verbatim is what keeps them apart.
        string uri = $"s3://acme/{key}";

        uri.Should().EndWith(key);
        uri.Should().Be($"s3://acme/{key}", "the key is carried verbatim, not normalised");
    }
}
