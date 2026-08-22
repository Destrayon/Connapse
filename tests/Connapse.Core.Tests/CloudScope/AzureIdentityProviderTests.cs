using Connapse.Core;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Connapse.Core.Tests.CloudScope;

[Trait("Category", "Unit")]
public class AzureIdentityProviderTests
{
    private readonly AzureIdentityProvider _provider = new(NullLogger<AzureIdentityProvider>.Instance);

    // The provider takes the remote's location as JSON since #353. It used to take a Container
    // and read its connector_config column; that column is gone, and the same fields are now
    // recombined from a connection's credential and its source's scope.
    private const string AzureConfig = """{"storageAccountName":"acct","containerName":"blobs"}""";

    [Fact]
    public async Task DiscoverScopesAsync_NullObjectId_ReturnsDeny()
    {
        var data = new CloudIdentityData(null, null, null, null, null);
        var result = await _provider.DiscoverScopesAsync(data, AzureConfig);

        result.HasAccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DiscoverScopesAsync_NullObjectId_DenyMessageMentionsProfile()
    {
        var data = new CloudIdentityData(null, null, null, null, null);
        var result = await _provider.DiscoverScopesAsync(data, AzureConfig);

        result.Error.Should().Contain("Profile");
    }

    [Fact]
    public async Task DiscoverScopesAsync_EmptyObjectId_ReturnsDeny()
    {
        var data = new CloudIdentityData(null, null, "", null, null);
        var result = await _provider.DiscoverScopesAsync(data, AzureConfig);

        result.HasAccess.Should().BeFalse();
    }

    [Fact]
    public async Task DiscoverScopesAsync_NullConnectorConfig_ReturnsDeny()
    {
        // Checked after the identity check, so this needs a linked identity to reach it.
        var data = new CloudIdentityData(null, null, "oid-123", "tid-456", "Test");
        var result = await _provider.DiscoverScopesAsync(data, connectorConfigJson: null);

        result.HasAccess.Should().BeFalse();
        result.Error.Should().Contain("configuration");
    }

    [Fact]
    public async Task DiscoverScopesAsync_ConfigWithoutStorageAccount_ReturnsDeny()
    {
        // Well-formed JSON that names no account. Distinct from the null case: the deserializer
        // succeeds and hands back an object whose required field is empty, which would otherwise
        // reach Azure as a request to "https://.blob.core.windows.net".
        var data = new CloudIdentityData(null, null, "oid-123", "tid-456", "Test");
        var result = await _provider.DiscoverScopesAsync(data, """{"containerName":"blobs"}""");

        result.HasAccess.Should().BeFalse();
        result.Error.Should().Contain("invalid");
    }
}
