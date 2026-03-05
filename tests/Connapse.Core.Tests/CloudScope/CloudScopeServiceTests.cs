using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Services;
using Connapse.Identity.Stores;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Connapse.Core.Tests.CloudScope;

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

    private static Container MakeContainer(ConnectorType type, string? config = null) => new(
        Id: Guid.NewGuid().ToString(),
        Name: "test",
        Description: null,
        ConnectorType: type,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow,
        ConnectorConfig: config);

    [Theory]
    [InlineData(ConnectorType.MinIO)]
    [InlineData(ConnectorType.Filesystem)]
    public async Task GetScopesAsync_NonCloudContainer_ReturnsNull(ConnectorType type)
    {
        var svc = CreateService();
        var result = await svc.GetScopesAsync(_userId, MakeContainer(type));
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetScopesAsync_S3Container_CacheHit_ReturnsCachedResult()
    {
        var container = MakeContainer(ConnectorType.S3);
        var cached = CloudScopeResult.FullAccess();
        _cache.GetAsync(_userId, Guid.Parse(container.Id)).Returns(cached);

        var svc = CreateService();
        var result = await svc.GetScopesAsync(_userId, container);

        result.Should().BeSameAs(cached);
        await _identityService.DidNotReceive().GetAsync(Arg.Any<Guid>(), Arg.Any<CloudProvider>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetScopesAsync_S3Container_NoIdentity_ReturnsDeny()
    {
        var container = MakeContainer(ConnectorType.S3);
        _cache.GetAsync(_userId, Guid.Parse(container.Id)).Returns((CloudScopeResult?)null);
        _identityService.GetAsync(_userId, CloudProvider.AWS, Arg.Any<CancellationToken>()).Returns((CloudIdentityDto?)null);

        var svc = CreateService();
        var result = await svc.GetScopesAsync(_userId, container);

        result.Should().NotBeNull();
        result!.HasAccess.Should().BeFalse();
        result.Error.Should().Contain("AWS");
        result.Error.Should().Contain("Cloud Identities");
    }

    [Fact]
    public async Task GetScopesAsync_AzureBlobContainer_WithIdentity_CallsProvider()
    {
        var container = MakeContainer(ConnectorType.AzureBlob);
        var identity = new CloudIdentityDto(
            Guid.NewGuid(), CloudProvider.Azure,
            new CloudIdentityData(null, null, "oid-123", "tid-456", "Test User"),
            DateTime.UtcNow, null);

        _cache.GetAsync(_userId, Guid.Parse(container.Id)).Returns((CloudScopeResult?)null);
        _identityService.GetAsync(_userId, CloudProvider.Azure, Arg.Any<CancellationToken>()).Returns(identity);
        _azureProvider.DiscoverScopesAsync(identity.Data, container, Arg.Any<CancellationToken>())
            .Returns(CloudScopeResult.FullAccess());

        var svc = CreateService();
        var result = await svc.GetScopesAsync(_userId, container);

        result.Should().NotBeNull();
        result!.HasAccess.Should().BeTrue();
        await _azureProvider.Received(1).DiscoverScopesAsync(identity.Data, container, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetScopesAsync_CachesResult_AfterProviderCall()
    {
        var container = MakeContainer(ConnectorType.S3);
        var identity = new CloudIdentityDto(
            Guid.NewGuid(), CloudProvider.AWS,
            new CloudIdentityData("arn:aws:iam::123:role/Test", "123", null, null, null),
            DateTime.UtcNow, null);

        _cache.GetAsync(_userId, Guid.Parse(container.Id)).Returns((CloudScopeResult?)null);
        _identityService.GetAsync(_userId, CloudProvider.AWS, Arg.Any<CancellationToken>()).Returns(identity);
        _awsProvider.DiscoverScopesAsync(identity.Data, container, Arg.Any<CancellationToken>())
            .Returns(CloudScopeResult.FullAccess());

        var svc = CreateService();
        await svc.GetScopesAsync(_userId, container);

        await _cache.Received(1).SetAsync(
            _userId,
            Guid.Parse(container.Id),
            Arg.Is<CloudScopeResult>(r => r.HasAccess),
            Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task GetScopesAsync_ProviderDeny_DoesNotUpdateLastUsed()
    {
        var container = MakeContainer(ConnectorType.AzureBlob);
        var identity = new CloudIdentityDto(
            Guid.NewGuid(), CloudProvider.Azure,
            new CloudIdentityData(null, null, "oid-123", "tid-456", "Test"),
            DateTime.UtcNow, null);

        _cache.GetAsync(_userId, Guid.Parse(container.Id)).Returns((CloudScopeResult?)null);
        _identityService.GetAsync(_userId, CloudProvider.Azure, Arg.Any<CancellationToken>()).Returns(identity);
        _azureProvider.DiscoverScopesAsync(identity.Data, container, Arg.Any<CancellationToken>())
            .Returns(CloudScopeResult.Deny("access denied"));

        var svc = CreateService();
        var result = await svc.GetScopesAsync(_userId, container);

        result!.HasAccess.Should().BeFalse();
        await _identityStore.DidNotReceive().UpdateLastUsedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetScopesAsync_ProviderAllow_UpdatesLastUsed()
    {
        var container = MakeContainer(ConnectorType.AzureBlob);
        var identityId = Guid.NewGuid();
        var identity = new CloudIdentityDto(
            identityId, CloudProvider.Azure,
            new CloudIdentityData(null, null, "oid-123", "tid-456", "Test"),
            DateTime.UtcNow, null);

        _cache.GetAsync(_userId, Guid.Parse(container.Id)).Returns((CloudScopeResult?)null);
        _identityService.GetAsync(_userId, CloudProvider.Azure, Arg.Any<CancellationToken>()).Returns(identity);
        _azureProvider.DiscoverScopesAsync(identity.Data, container, Arg.Any<CancellationToken>())
            .Returns(CloudScopeResult.FullAccess());

        var svc = CreateService();
        await svc.GetScopesAsync(_userId, container);

        // Give the fire-and-forget task a moment
        await Task.Delay(100);
        await _identityStore.Received(1).UpdateLastUsedAsync(identityId, Arg.Any<CancellationToken>());
    }
}
