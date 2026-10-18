using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Connectors.Custom;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

/// <summary>
/// The same connector, on a custom connection: its documents carry a <c>custom://</c> resource URI and the
/// access keys the middle man listed, so search can check them. Without a connection they carry neither.
/// </summary>
[Trait("Category", "Unit")]
public class CustomSourceConnectedTests
{
    private static readonly Guid SourceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly FakeMiddleMan _remote = new();

    private CustomSourceConnector Connector(Guid? permissionsFor) => new(
        new CustomSourceClient(new HttpClient(_remote), FakeMiddleMan.BaseUrl, FakeMiddleMan.Token),
        new CustomSourceScope(FakeMiddleMan.BaseUrl, FakeMiddleMan.Scope),
        permissionsFor);

    [Fact]
    public async Task Connected_FilesCarryAResourceUriAndTheirAccessKeys()
    {
        _remote.Put("C1/msg 2", "design/thread.md", "x");

        ConnectorFile file = (await Connector(SourceId).GetChangesAsync(null)).Upserted.Single();

        file.ResourceUri.Should().Be($"custom://{SourceId}/{Uri.EscapeDataString("C1/msg 2")}");
        file.AccessKeys.Should().Equal("folder:design");
        CustomResourceUri.TryParse(file.ResourceUri, out Guid sourceId, out string documentId).Should().BeTrue();
        sourceId.Should().Be(SourceId);
        documentId.Should().Be("C1/msg 2");
    }

    [Fact]
    public async Task Connected_DocumentTheMiddleManListsNoKeysFor_CarriesAnEmptyKeySet()
    {
        _remote.SupportsAccessKeys = false;
        _remote.Put("a", "a.md", "x");

        ConnectorFile file = (await Connector(SourceId).GetChangesAsync(null)).Upserted.Single();

        file.AccessKeys.Should().BeEmpty();
        file.ResourceUri.Should().NotBeNull();
    }

    [Fact]
    public async Task ContentOnly_FilesCarryNeitherUriNorKeys()
    {
        _remote.Put("a", "a.md", "x");

        ConnectorFile file = (await Connector(permissionsFor: null).GetChangesAsync(null)).Upserted.Single();

        file.ResourceUri.Should().BeNull();
        file.AccessKeys.Should().BeNull();
    }

    [Theory]
    [InlineData("custom://not-a-guid/a")]
    [InlineData("custom://11111111-2222-3333-4444-555555555555/")]
    [InlineData("s3://bucket/key")]
    [InlineData("")]
    public void ResourceUri_Malformed_DoesNotParse(string uri)
    {
        CustomResourceUri.TryParse(uri, out _, out _).Should().BeFalse();
    }
}
