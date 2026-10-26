using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Connapse.Storage.Connectors;
using Connapse.Storage.FileSystem;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Connapse.Core.Tests.Connectors;

/// <summary>
/// A HEAD 404 can't say whether the bucket or the key is missing, so MinioConnector asks the bucket.
/// The documented minimal policy grants s3:ListBucket but not s3:GetBucketLocation, so the probe
/// must be HeadBucket, or every missing file under that policy reads as a storage error (CON-101).
/// </summary>
[Trait("Category", "Unit")]
public class MinioConnectorBucketProbeTests
{
    private readonly IAmazonS3 _s3 = Substitute.For<IAmazonS3>();

    private MinioConnector Connector() => new(
        _s3,
        Options.Create(new MinioOptions { BucketName = "knowledge-files" }),
        new MinioConnectorConfig { ContainerId = "c1" });

    private static AmazonS3Exception Status(HttpStatusCode code) => new("error") { StatusCode = code };

    [Fact]
    public async Task ExistsAsync_HeadNotFoundAndBucketExists_ReturnsFalseWithoutBucketLocation()
    {
        _s3.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Status(HttpStatusCode.NotFound));
        _s3.GetBucketLocationAsync(Arg.Any<GetBucketLocationRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Status(HttpStatusCode.Forbidden));

        (await Connector().ExistsAsync("/docs/a.txt")).Should().BeFalse();
        await _s3.Received(1).HeadBucketAsync(
            Arg.Is<HeadBucketRequest>(r => r.BucketName == "knowledge-files"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExistsAsync_HeadNotFoundAndBucketNotFound_ThrowsBucketNotFound()
    {
        _s3.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Status(HttpStatusCode.NotFound));
        _s3.HeadBucketAsync(Arg.Any<HeadBucketRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Status(HttpStatusCode.NotFound));

        Func<Task> act = () => Connector().ExistsAsync("/docs/a.txt");

        (await act.Should().ThrowAsync<StorageBucketNotFoundException>())
            .Which.BucketName.Should().Be("knowledge-files");
    }
}
