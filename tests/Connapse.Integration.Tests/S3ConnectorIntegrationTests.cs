using System.Text;
using Amazon.S3.Model;
using Connapse.Storage.Connectors;
using FluentAssertions;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for S3Connector against LocalStack.
/// Each test run creates a unique bucket for isolation.
/// </summary>
[Trait("Category", "Integration")]
[Collection("S3 Connector Tests")]
public class S3ConnectorIntegrationTests : IAsyncLifetime
{
    private readonly LocalStackFixture _localStack;
    private S3Connector _connector = null!;
    private string _bucketName = null!;

    public S3ConnectorIntegrationTests(LocalStackFixture localStack)
    {
        _localStack = localStack;
    }

    public async Task InitializeAsync()
    {
        _bucketName = $"connapse-test-{Guid.NewGuid():N}"[..32];
        await _localStack.CreateBucketAsync(_bucketName);

        var config = new S3ConnectorConfig
        {
            BucketName = _bucketName,
            Region = LocalStackFixture.Region
        };

        _connector = new S3Connector(config);
    }

    public async Task DisposeAsync()
    {
        var listResponse = await _localStack.S3Client.ListObjectsV2Async(
            new ListObjectsV2Request { BucketName = _bucketName });

        foreach (var obj in listResponse.S3Objects ?? [])
        {
            await _localStack.S3Client.DeleteObjectAsync(_bucketName, obj.Key);
        }

        await _localStack.S3Client.DeleteBucketAsync(_bucketName);
        _connector.Dispose();
    }


    /// <summary>
    /// Seeds an object directly through the AWS SDK. S3Connector is read-only as of #351
    /// (external storage backs sources, which Connapse never mutates), so fixtures can no
    /// longer be created through the connector itself.
    /// </summary>
    private async Task SeedAsync(string virtualPath, string content, string? contentType = null)
    {
        await _localStack.S3Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = virtualPath.TrimStart('/'),
            ContentBody = content,
            ContentType = contentType
        });
    }

    [Fact]
    public async Task ListFilesAsync_EmptyBucket_ReturnsEmpty()
    {
        var files = await _connector.ListFilesAsync();

        files.Should().BeEmpty("no files have been uploaded to a fresh bucket");
    }

    [Fact]
    public async Task ListFilesAsync_SingleSeededFile_ReturnsFile()
    {
        const string body = "Hello from S3 connector test";
        await SeedAsync("/docs/hello.txt", body, "text/plain");

        var files = await _connector.ListFilesAsync();

        files.Should().HaveCount(1);
        files[0].Path.Should().Be("/docs/hello.txt");
        files[0].SizeBytes.Should().Be(Encoding.UTF8.GetByteCount(body));
    }

    [Fact]
    public async Task ReadFileAsync_ExistingFile_ReturnsContent()
    {
        const string originalContent = "S3 connector read test content";
        await SeedAsync("/data/readtest.txt", originalContent);

        using var readStream = await _connector.ReadFileAsync("/data/readtest.txt");
        using var reader = new StreamReader(readStream);
        var readContent = await reader.ReadToEndAsync();

        readContent.Should().Be(originalContent);
    }

    [Fact]
    public async Task ReadFileAsync_NonExistentFile_ThrowsFileNotFoundException()
    {
        var act = async () => await _connector.ReadFileAsync("/does/not/exist.txt");

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task ExistsAsync_ExistingFile_ReturnsTrue()
    {
        await SeedAsync("/check/exists.txt", "exists check");

        var exists = await _connector.ExistsAsync("/check/exists.txt");

        exists.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_NonExistentFile_ReturnsFalse()
    {
        var exists = await _connector.ExistsAsync("/no/such/file.txt");

        exists.Should().BeFalse();
    }

    [Fact]
    public async Task ListFilesAsync_WithPrefix_FiltersCorrectly()
    {
        await SeedAsync("/docs/doc1.txt", "doc1");
        await SeedAsync("/docs/doc2.txt", "doc2");
        await SeedAsync("/images/img1.png", "img1");

        var docsOnly = await _connector.ListFilesAsync("docs");

        docsOnly.Should().HaveCount(2);
        docsOnly.Should().AllSatisfy(f => f.Path.Should().StartWith("/docs/"));
    }

    [Fact]
    public async Task ListFilesAsync_MultipleSeededFiles_AllReturned()
    {
        var fileNames = new[] { "alpha.txt", "beta.txt", "gamma.txt" };
        foreach (var name in fileNames)
        {
            await SeedAsync($"/{name}", $"content of {name}");
        }

        var listed = await _connector.ListFilesAsync();

        listed.Should().HaveCount(3);
        listed.Select(f => f.Path).Should().BeEquivalentTo(fileNames.Select(n => $"/{n}"));
    }
}
