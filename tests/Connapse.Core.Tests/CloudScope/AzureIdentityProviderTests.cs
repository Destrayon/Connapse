using Connapse.Core;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Connapse.Core.Tests.CloudScope;

[Trait("Category", "Unit")]
public class AzureIdentityProviderTests
{
    private readonly AzureIdentityProvider _provider = new(NullLogger<AzureIdentityProvider>.Instance);

    private static Container MakeAzureBlobContainer(string? config = null) => new(
        Id: Guid.NewGuid().ToString(),
        Name: "azure-test",
        Description: null,
        ConnectorType: ConnectorType.AzureBlob,
        IsEphemeral: false,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow,
        ConnectorConfig: config ?? """{"storageAccountName":"test","containerName":"docs"}""");

    [Fact]
    public async Task DiscoverScopesAsync_NullObjectId_ReturnsDeny()
    {
        var data = new CloudIdentityData(null, null, null, null, null);
        var result = await _provider.DiscoverScopesAsync(data, MakeAzureBlobContainer());

        result.HasAccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DiscoverScopesAsync_NullObjectId_DenyMessageMentionsProfile()
    {
        var data = new CloudIdentityData(null, null, null, null, null);
        var result = await _provider.DiscoverScopesAsync(data, MakeAzureBlobContainer());

        result.Error.Should().Contain("Profile");
    }

    [Fact]
    public async Task DiscoverScopesAsync_EmptyObjectId_ReturnsDeny()
    {
        var data = new CloudIdentityData(null, null, "", null, null);
        var result = await _provider.DiscoverScopesAsync(data, MakeAzureBlobContainer());

        result.HasAccess.Should().BeFalse();
    }

    [Fact]
    public async Task DiscoverScopesAsync_NullConnectorConfig_ReturnsDeny()
    {
        var data = new CloudIdentityData(null, null, "oid-123", "tid-456", "Test");
        var result = await _provider.DiscoverScopesAsync(data, MakeAzureBlobContainer(config: null));

        // null config container — passes non-null to factory, but let's test with explicit null
        var container = new Container(
            Id: Guid.NewGuid().ToString(), Name: "test", Description: null,
            ConnectorType: ConnectorType.AzureBlob, IsEphemeral: false,
            CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow, ConnectorConfig: null);

        result = await _provider.DiscoverScopesAsync(data, container);
        result.HasAccess.Should().BeFalse();
        result.Error.Should().Contain("configuration");
    }
}
