using Azure.Storage.Blobs;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

[Trait("Category", "Unit")]
public class AzureBlobConnectorUnitTests
{
    private static AzureBlobConnector Make() =>
        new(new AzureBlobConnectorConfig { AccountName = "acct", ContainerName = "docs", Prefix = "reports/" },
            new BlobServiceClient(new Uri("http://127.0.0.1:10000/devstoreaccount1")));

    [Fact] public void Type_IsAzureBlob() => Make().Type.Should().Be(ConnectorType.AzureBlob);
    [Fact] public void SupportsLiveWatch_False() => Make().SupportsLiveWatch.Should().BeFalse();

    [Fact]
    public void ResolveJobPath_JoinsUnderPrefix()
        => Make().ResolveJobPath("q1.pdf").Should().Be("reports/q1.pdf");

    [Fact]
    public async Task WatchAsync_Throws()
    {
        var act = async () => { await foreach (var _ in Make().WatchAsync()) { } };
        await act.Should().ThrowAsync<NotSupportedException>();
    }
}
