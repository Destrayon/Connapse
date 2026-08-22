using Connapse.Core;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Connapse.Core.Tests.CloudScope;

[Trait("Category", "Unit")]
public class AwsIdentityProviderTests
{
    private readonly AwsIdentityProvider _provider = new(NullLogger<AwsIdentityProvider>.Instance);

    // The AWS provider never reads the location: it decides on the SSO account set alone, and
    // the bucket is irrelevant to that. Passed explicitly so the parameter's presence is
    // visible rather than defaulted away.
    private const string S3Config = """{"region":"us-east-1","bucketName":"b"}""";

    [Fact]
    public async Task DiscoverScopesAsync_NullPrincipalArn_ReturnsDeny()
    {
        var data = new CloudIdentityData(null, null, null, null, null);
        var result = await _provider.DiscoverScopesAsync(data, S3Config);

        result.HasAccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DiscoverScopesAsync_NullPrincipalArn_DenyMessageMentionsSso()
    {
        var data = new CloudIdentityData(null, null, null, null, null);
        var result = await _provider.DiscoverScopesAsync(data, S3Config);

        result.Error.Should().Contain("SSO");
    }

    [Fact]
    public async Task DiscoverScopesAsync_EmptyPrincipalArn_ReturnsDeny()
    {
        var data = new CloudIdentityData("", null, null, null, null);
        var result = await _provider.DiscoverScopesAsync(data, S3Config);

        result.HasAccess.Should().BeFalse();
    }
}
