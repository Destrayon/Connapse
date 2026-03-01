using Connapse.Core;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Connapse.Core.Tests.CloudScope;

[Trait("Category", "Unit")]
public class AwsIdentityProviderTests
{
    private readonly AwsIdentityProvider _provider = new(NullLogger<AwsIdentityProvider>.Instance);

    private static Container MakeS3Container() => new(
        Id: Guid.NewGuid().ToString(),
        Name: "s3-test",
        Description: null,
        ConnectorType: ConnectorType.S3,
        IsEphemeral: false,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow,
        ConnectorConfig: """{"bucketName":"test","region":"us-east-1"}""");

    [Fact]
    public async Task DiscoverScopesAsync_NullPrincipalArn_ReturnsDeny()
    {
        var data = new CloudIdentityData(null, null, null, null, null);
        var result = await _provider.DiscoverScopesAsync(data, MakeS3Container());

        result.HasAccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DiscoverScopesAsync_NullPrincipalArn_DenyMessageMentionsRS256()
    {
        var data = new CloudIdentityData(null, null, null, null, null);
        var result = await _provider.DiscoverScopesAsync(data, MakeS3Container());

        result.Error.Should().Contain("RS256");
    }

    [Fact]
    public async Task DiscoverScopesAsync_EmptyPrincipalArn_ReturnsDeny()
    {
        var data = new CloudIdentityData("", null, null, null, null);
        var result = await _provider.DiscoverScopesAsync(data, MakeS3Container());

        result.HasAccess.Should().BeFalse();
    }
}
