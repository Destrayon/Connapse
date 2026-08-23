using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Services;
using Connapse.Identity.Stores;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Connapse.Core.Tests.CloudScope;

/// <summary>
/// Cloud scope moved from containers to sources in #353. A container is managed storage —
/// Connapse's own backend, with no external IAM to consult — so pointing this service at one
/// could only ever return null. Every case below is keyed on a source and its connection.
/// </summary>
[Trait("Category", "Unit")]
public class CloudScopeServiceTests
{
    private readonly ICloudIdentityProvider _awsProvider = Substitute.For<ICloudIdentityProvider>();
    private readonly ICloudIdentityProvider _azureProvider = Substitute.For<ICloudIdentityProvider>();
    private readonly IConnectorScopeCache _cache = Substitute.For<IConnectorScopeCache>();
    private readonly ICloudIdentityService _identityService = Substitute.For<ICloudIdentityService>();
    private readonly ICloudIdentityStore _identityStore = Substitute.For<ICloudIdentityStore>();
    private readonly Guid _userId = Guid.NewGuid();

    public CloudScopeServiceTests()
    {
        _awsProvider.Provider.Returns(CloudProvider.AWS);
        _azureProvider.Provider.Returns(CloudProvider.Azure);
    }

    private CloudScopeService CreateService() => new(
        [_awsProvider, _azureProvider],
        _cache,
        _identityService,
        _identityStore,
        NullLogger<CloudScopeService>.Instance);

    private static (Source Source, Connection Connection) MakePair(
        ConnectionProvider provider,
        string? connectionConfig = null,
        string? scope = null)
    {
        var connectionId = Guid.NewGuid();

        var connection = new Connection(
            Id: connectionId,
            Name: "conn",
            Provider: provider,
            ConfigJson: connectionConfig,
            CreatedByUserId: null,
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow);

        var source = new Source(
            Id: Guid.NewGuid(),
            Name: "src",
            Description: null,
            ConnectionId: connectionId,
            // Matches what the create route stores for a source with no scope, so the tests
            // that omit one exercise the same value production would hold.
            ScopeJson: scope ?? "{}",
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow);

        return (source, connection);
    }

    [Fact]
    public async Task GetScopesAsync_FilesystemSource_ReturnsNull()
    {
        var (source, connection) = MakePair(ConnectionProvider.Filesystem);

        var result = await CreateService().GetScopesAsync(_userId, source, connection);

        result.Should().BeNull("a filesystem source is local — role-level RBAC is the whole story");
    }

    [Fact]
    public async Task GetScopesAsync_ConnectionDoesNotOwnSource_Throws()
    {
        var (source, _) = MakePair(ConnectionProvider.S3);
        var (_, otherConnection) = MakePair(ConnectionProvider.S3);

        var act = async () => await CreateService().GetScopesAsync(_userId, source, otherConnection);

        // Silently discovering scope against the wrong credential would grant or deny access on
        // the basis of an account that has nothing to do with this source.
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetScopesAsync_S3Source_CacheHit_ReturnsCachedResult()
    {
        var (source, connection) = MakePair(ConnectionProvider.S3);
        var cached = CloudScopeResult.FullAccess();
        _cache.GetAsync(_userId, source.Id).Returns(cached);

        var result = await CreateService().GetScopesAsync(_userId, source, connection);

        result.Should().BeSameAs(cached);
        await _identityService.DidNotReceive().GetAsync(Arg.Any<Guid>(), Arg.Any<CloudProvider>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetScopesAsync_S3Source_NoIdentity_ReturnsDeny()
    {
        var (source, connection) = MakePair(ConnectionProvider.S3);
        _cache.GetAsync(_userId, source.Id).Returns((CloudScopeResult?)null);
        _identityService.GetAsync(_userId, CloudProvider.AWS, Arg.Any<CancellationToken>()).Returns((CloudIdentityDto?)null);

        var result = await CreateService().GetScopesAsync(_userId, source, connection);

        result.Should().NotBeNull();
        result!.HasAccess.Should().BeFalse();
        result.Error.Should().Contain("AWS");
        result.Error.Should().Contain("Cloud Identities");
    }

    [Fact]
    public async Task GetScopesAsync_AzureSource_WithIdentity_CallsProvider()
    {
        var (source, connection) = MakePair(ConnectionProvider.AzureBlob);
        var identity = MakeAzureIdentity();

        _cache.GetAsync(_userId, source.Id).Returns((CloudScopeResult?)null);
        _identityService.GetAsync(_userId, CloudProvider.Azure, Arg.Any<CancellationToken>()).Returns(identity);
        _azureProvider.DiscoverScopesAsync(identity.Data, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(CloudScopeResult.FullAccess());

        var result = await CreateService().GetScopesAsync(_userId, source, connection);

        result.Should().NotBeNull();
        result!.HasAccess.Should().BeTrue();
        await _azureProvider.Received(1).DiscoverScopesAsync(identity.Data, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetScopesAsync_AzureSource_PassesConnectionCredentialAndSourceScopeMerged()
    {
        // The provider needs one object holding both halves: the account comes off the
        // connection, the blob container off the source's scope. Splitting them across two rows
        // is the whole point of the connection/source model, so recombining them correctly is
        // the part that can silently regress.
        var (source, connection) = MakePair(
            ConnectionProvider.AzureBlob,
            connectionConfig: """{"storageAccountName":"acct"}""",
            scope: """{"containerName":"blobs","prefix":"team-a/"}""");

        var identity = MakeAzureIdentity();
        _cache.GetAsync(_userId, source.Id).Returns((CloudScopeResult?)null);
        _identityService.GetAsync(_userId, CloudProvider.Azure, Arg.Any<CancellationToken>()).Returns(identity);
        _azureProvider.DiscoverScopesAsync(identity.Data, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(CloudScopeResult.FullAccess());

        await CreateService().GetScopesAsync(_userId, source, connection);

        await _azureProvider.Received(1).DiscoverScopesAsync(
            identity.Data,
            Arg.Is<string?>(json =>
                json != null
                && json.Contains("acct")
                && json.Contains("blobs")
                && json.Contains("team-a/")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetScopesAsync_SourceScopeOverridesConnectionOnConflict()
    {
        // A connection that also names a container must not widen the source past its own
        // scope. The source is the narrower statement, so it wins.
        var (source, connection) = MakePair(
            ConnectionProvider.AzureBlob,
            connectionConfig: """{"storageAccountName":"acct","containerName":"everything"}""",
            scope: """{"containerName":"just-mine"}""");

        var identity = MakeAzureIdentity();
        _cache.GetAsync(_userId, source.Id).Returns((CloudScopeResult?)null);
        _identityService.GetAsync(_userId, CloudProvider.Azure, Arg.Any<CancellationToken>()).Returns(identity);
        _azureProvider.DiscoverScopesAsync(identity.Data, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(CloudScopeResult.FullAccess());

        await CreateService().GetScopesAsync(_userId, source, connection);

        await _azureProvider.Received(1).DiscoverScopesAsync(
            identity.Data,
            Arg.Is<string?>(json => json != null && json.Contains("just-mine") && !json.Contains("everything")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetScopesAsync_CachesResult_AfterProviderCall()
    {
        var (source, connection) = MakePair(ConnectionProvider.S3);
        var identity = new CloudIdentityDto(
            Guid.NewGuid(), CloudProvider.AWS,
            new CloudIdentityData("arn:aws:iam::123:role/Test", "123", null, null, null),
            DateTime.UtcNow, null);

        _cache.GetAsync(_userId, source.Id).Returns((CloudScopeResult?)null);
        _identityService.GetAsync(_userId, CloudProvider.AWS, Arg.Any<CancellationToken>()).Returns(identity);
        _awsProvider.DiscoverScopesAsync(identity.Data, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(CloudScopeResult.FullAccess());

        await CreateService().GetScopesAsync(_userId, source, connection);

        // Keyed on the source, not the connection: two sources sharing one connection point at
        // different buckets, so caching per connection would leak one's verdict onto the other.
        await _cache.Received(1).SetAsync(
            _userId,
            source.Id,
            Arg.Is<CloudScopeResult>(r => r.HasAccess),
            Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task GetScopesAsync_ProviderDeny_DoesNotUpdateLastUsed()
    {
        var (source, connection) = MakePair(ConnectionProvider.AzureBlob);
        var identity = MakeAzureIdentity();

        _cache.GetAsync(_userId, source.Id).Returns((CloudScopeResult?)null);
        _identityService.GetAsync(_userId, CloudProvider.Azure, Arg.Any<CancellationToken>()).Returns(identity);
        _azureProvider.DiscoverScopesAsync(identity.Data, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(CloudScopeResult.Deny("access denied"));

        var result = await CreateService().GetScopesAsync(_userId, source, connection);

        result!.HasAccess.Should().BeFalse();
        await _identityStore.DidNotReceive().UpdateLastUsedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetScopesAsync_ProviderAllow_UpdatesLastUsed()
    {
        var (source, connection) = MakePair(ConnectionProvider.AzureBlob);
        var identityId = Guid.NewGuid();
        var identity = new CloudIdentityDto(
            identityId, CloudProvider.Azure,
            new CloudIdentityData(null, null, "oid-123", "tid-456", "Test"),
            DateTime.UtcNow, null);

        _cache.GetAsync(_userId, source.Id).Returns((CloudScopeResult?)null);
        _identityService.GetAsync(_userId, CloudProvider.Azure, Arg.Any<CancellationToken>()).Returns(identity);
        _azureProvider.DiscoverScopesAsync(identity.Data, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(CloudScopeResult.FullAccess());

        await CreateService().GetScopesAsync(_userId, source, connection);

        await _identityStore.Received(1).UpdateLastUsedAsync(identityId, Arg.Any<CancellationToken>());
    }

    private static CloudIdentityDto MakeAzureIdentity() => new(
        Guid.NewGuid(), CloudProvider.Azure,
        new CloudIdentityData(null, null, "oid-123", "tid-456", "Test User"),
        DateTime.UtcNow, null);
}
