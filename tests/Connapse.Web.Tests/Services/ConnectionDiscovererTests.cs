using Connapse.Application.Connections;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.GitHub;
using Connapse.Web.Services;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Web.Tests.Services;

/// <summary>
/// connections.discover's lookups: Connapse's Azure token is only sent to the named account's own endpoint, and each
/// provider's answer becomes the note the Connections page shows.
/// </summary>
[Trait("Category", "Unit")]
public class ConnectionDiscovererTests
{
    private readonly IS3Discovery _s3 = Substitute.For<IS3Discovery>();
    private readonly IAzureBlobDiscovery _azure = Substitute.For<IAzureBlobDiscovery>();
    private readonly IGitHubAppInstallations _gitHub = Substitute.For<IGitHubAppInstallations>();
    private readonly ICustomProviderStore _customProviders = Substitute.For<ICustomProviderStore>();

    // The Atlassian site service is never reached by these.
    private ConnectionDiscoverer Discoverer() =>
        new(_s3, _azure, _gitHub, Substitute.For<IProviderCredentialStore>(), _customProviders, null!);

    private static Connection Stored(ConnectionProvider provider, string configJson) =>
        new(Guid.NewGuid(), "stored", provider, configJson, null, DateTime.UtcNow, DateTime.UtcNow, HasSecret: true);

    [Theory]
    [InlineData("https://attacker.example")]
    [InlineData("https://other.blob.core.windows.net")]
    public async Task DraftContainers_EndpointNotTheAccountsOwn_IsRefusedWithoutAsking(string endpoint)
    {
        var found = await Discoverer().DiscoverDraftAsync(new DraftDiscovery(ConnectionProvider.AzureBlob,
            StorageAccountName: "mine", BlobEndpoint: endpoint));

        found.Status.Should().Be(ConnectionStatus.Invalid);
        found.Field.Should().Be("blobEndpoint");
        await _azure.DidNotReceiveWithAnyArgs().ListContainersAsync(default!, default);
    }

    [Fact]
    public async Task DraftContainers_AccountNamed_ListsItsOwnEndpoint()
    {
        _azure.ListContainersAsync("https://mine.blob.core.windows.net", Arg.Any<CancellationToken>())
            .Returns(AzureProbe<IReadOnlyList<string>>.Ok(["docs"]));

        var found = await Discoverer().DiscoverDraftAsync(new DraftDiscovery(ConnectionProvider.AzureBlob, StorageAccountName: "mine"));

        found.Result!.Containers.Should().Equal("docs");
    }

    [Fact]
    public async Task StoredAzure_EndpointNotTheAccountsOwn_ListsOnlyAccounts()
    {
        _azure.ListStorageAccountsAsync(Arg.Any<CancellationToken>()).Returns(AzureProbe<IReadOnlyList<AzureStorageAccountInfo>>.Ok([]));
        Connection stored = Stored(ConnectionProvider.AzureBlob,
            """{"accountName":"mine","blobEndpoint":"https://attacker.example","containerName":"c"}""");

        var found = await Discoverer().DiscoverAsync(stored, new ConnectionDiscoveryRequest());

        found.Result!.StorageAccounts.Should().BeEmpty();
        found.Result.Containers.Should().BeNull();
        await _azure.DidNotReceiveWithAnyArgs().ListContainersAsync(default!, default);
    }

    [Fact]
    public async Task StoredS3_Denied_IsANoteNotAFailure()
    {
        _s3.ListBucketsAsync("arn:aws:iam::1:role/r", Arg.Any<CancellationToken>()).Returns(AwsProbe<IReadOnlyList<string>>.Denied("no"));
        Connection stored = Stored(ConnectionProvider.S3, """{"region":"us-east-1","roleArn":"arn:aws:iam::1:role/r"}""");

        var found = await Discoverer().DiscoverAsync(stored, new ConnectionDiscoveryRequest());

        found.Succeeded.Should().BeTrue();
        found.Result!.Outcome.Should().Be(DiscoveryOutcome.Denied);
        found.Result.Message.Should().Be("This identity may not list buckets, so type the name instead.");
    }

    [Fact]
    public async Task GitHub_NoApp_IsNotConfigured()
    {
        _gitHub.IsConfiguredAsync(Arg.Any<CancellationToken>()).Returns(false);

        var found = await Discoverer().DiscoverDraftAsync(new DraftDiscovery(ConnectionProvider.GitHub));

        found.Result!.Outcome.Should().Be(DiscoveryOutcome.NotConfigured);
        await _gitHub.DidNotReceiveWithAnyArgs().ListInstallationsAsync(default);
    }

    [Theory]
    [InlineData(ConnectionProvider.Sftp)]
    [InlineData(ConnectionProvider.Custom)]
    public async Task Stored_ProviderWithNothingToList_IsInvalid(ConnectionProvider provider)
    {
        var found = await Discoverer().DiscoverAsync(Stored(provider, "{}"), new ConnectionDiscoveryRequest());

        found.Status.Should().Be(ConnectionStatus.Invalid);
    }
}
