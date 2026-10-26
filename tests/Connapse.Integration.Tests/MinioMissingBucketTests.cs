using Amazon.S3;
using Connapse.Core;
using Connapse.Storage.Connectors;
using Connapse.Storage.FileSystem;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Connapse.Integration.Tests;

/// <summary>
/// A missing bucket is a setup problem, not a missing file (CON-101). These run against the
/// fixture's real MinIO because the point is what MinIO answers: a HEAD 404 has no error body,
/// so a missing bucket and a missing key look the same on the wire.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class MinioMissingBucketTests(SharedWebAppFixture fixture)
{
    private MinioConnector Connector(string bucketName)
    {
        var s3 = fixture.Factory.Services.GetRequiredService<IAmazonS3>();
        var options = fixture.Factory.Services.GetRequiredService<IOptions<MinioOptions>>().Value;
        var copy = new MinioOptions
        {
            Endpoint = options.Endpoint,
            AccessKey = options.AccessKey,
            SecretKey = options.SecretKey,
            UseSSL = options.UseSSL,
            BucketName = bucketName,
        };
        return new MinioConnector(s3, Options.Create(copy), new MinioConnectorConfig { ContainerId = Guid.NewGuid().ToString() });
    }

    private static string MissingBucket() => $"no-such-bucket-{Guid.NewGuid():N}"[..32];

    private string RealBucket() =>
        fixture.Factory.Services.GetRequiredService<IOptions<MinioOptions>>().Value.BucketName;

    [Fact]
    public async Task ExistsAsync_BucketMissing_ThrowsStorageErrorNamingTheBucket()
    {
        string bucket = MissingBucket();

        Func<Task> act = () => Connector(bucket).ExistsAsync("/docs/a.txt");

        (await act.Should().ThrowAsync<StorageBucketNotFoundException>())
            .Which.Message.Should().Contain(bucket);
    }

    [Fact]
    public async Task ExistsAsync_KeyMissingInExistingBucket_ReturnsFalse()
    {
        (await Connector(RealBucket()).ExistsAsync("/no/such/file.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task ReadFileAsync_BucketMissing_ThrowsStorageErrorNamingTheBucket()
    {
        string bucket = MissingBucket();

        Func<Task> act = () => Connector(bucket).ReadFileAsync("/docs/a.txt");

        (await act.Should().ThrowAsync<StorageBucketNotFoundException>())
            .Which.BucketName.Should().Be(bucket);
    }

    [Fact]
    public async Task ReadFileAsync_KeyMissingInExistingBucket_ThrowsFileNotFound()
    {
        Func<Task> act = () => Connector(RealBucket()).ReadFileAsync("/no/such/file.txt");

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task DeleteFileAsync_BucketMissing_ThrowsStorageErrorNamingTheBucket()
    {
        string bucket = MissingBucket();

        Func<Task> act = () => Connector(bucket).DeleteFileAsync("/docs/a.txt");

        (await act.Should().ThrowAsync<StorageBucketNotFoundException>())
            .Which.BucketName.Should().Be(bucket);
    }

    [Fact]
    public async Task DeleteFileAsync_KeyMissingInExistingBucket_Succeeds()
    {
        Func<Task> act = () => Connector(RealBucket()).DeleteFileAsync("/no/such/file.txt");

        await act.Should().NotThrowAsync();
    }
}
