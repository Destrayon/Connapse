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

    [Fact]
    public async Task ReadFileAsync_OutOfPrefixPath_ThrowsUnauthorizedAccessException()
    {
        var act = async () => await Make().ReadFileAsync("hr/secret.pdf");
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ExistsAsync_OutOfPrefixPath_ReturnsFalse()
        => (await Make().ExistsAsync("hr/secret.pdf")).Should().BeFalse();

    private static AzureBlobConnector MakeWithPrefix(string? prefix) =>
        new(new AzureBlobConnectorConfig { AccountName = "acct", ContainerName = "docs", Prefix = prefix },
            new BlobServiceClient(new Uri("http://127.0.0.1:10000/devstoreaccount1")));

    [Fact]
    public void IsInPrefixScope_SiblingPrefix_IsRejected()
        => MakeWithPrefix("team/").IsInPrefixScope("team-archive/secret.txt").Should().BeFalse();

    [Fact]
    public void IsInPrefixScope_MatchingSubtree_IsAllowed()
        => MakeWithPrefix("team/").IsInPrefixScope("team/ok.txt").Should().BeTrue();

    [Fact]
    public async Task ReadFileAsync_SiblingPrefixPath_ThrowsUnauthorizedAccessException()
    {
        var act = async () => await MakeWithPrefix("team/").ReadFileAsync("team-archive/secret.txt");
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ExistsAsync_SiblingPrefixPath_ReturnsFalse()
        => (await MakeWithPrefix("team/").ExistsAsync("team-archive/secret.txt")).Should().BeFalse();

    [Fact]
    public void IsInPrefixScope_EmptyPrefix_AllowsAnyPath()
        => MakeWithPrefix(null).IsInPrefixScope("anything/at/all.txt").Should().BeTrue();
}
