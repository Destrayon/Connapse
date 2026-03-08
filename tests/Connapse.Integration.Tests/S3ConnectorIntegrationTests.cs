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

    [Fact]
    public async Task ListFilesAsync_EmptyBucket_ReturnsEmpty()
    {
        var files = await _connector.ListFilesAsync();

        files.Should().BeEmpty("no files have been uploaded to a fresh bucket");
    }

    [Fact]
    public async Task WriteAndListFilesAsync_SingleFile_ReturnsFile()
    {
        var content = Encoding.UTF8.GetBytes("Hello from S3 connector test");
        using var stream = new MemoryStream(content);

        await _connector.WriteFileAsync("/docs/hello.txt", stream, "text/plain");
        var files = await _connector.ListFilesAsync();

        files.Should().HaveCount(1);
        files[0].Path.Should().Be("/docs/hello.txt");
        files[0].SizeBytes.Should().Be(content.Length);
    }

    [Fact]
    public async Task ReadFileAsync_ExistingFile_ReturnsContent()
    {
        const string originalContent = "S3 connector read test content";
        using var writeStream = new MemoryStream(Encoding.UTF8.GetBytes(originalContent));
        await _connector.WriteFileAsync("/data/readtest.txt", writeStream);

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
    public async Task DeleteFileAsync_ExistingFile_RemovesFromBucket()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("to be deleted"));
        await _connector.WriteFileAsync("/temp/delete-me.txt", stream);

        var filesBefore = await _connector.ListFilesAsync();
        filesBefore.Should().ContainSingle(f => f.Path == "/temp/delete-me.txt");

        await _connector.DeleteFileAsync("/temp/delete-me.txt");

        var filesAfter = await _connector.ListFilesAsync();
        filesAfter.Should().NotContain(f => f.Path == "/temp/delete-me.txt");
    }

    [Fact]
    public async Task DeleteFileAsync_NonExistentFile_DoesNotThrow()
    {
        var act = async () => await _connector.DeleteFileAsync("/ghost/file.txt");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ExistsAsync_ExistingFile_ReturnsTrue()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("exists check"));
        await _connector.WriteFileAsync("/check/exists.txt", stream);

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
        using var s1 = new MemoryStream(Encoding.UTF8.GetBytes("doc1"));
        using var s2 = new MemoryStream(Encoding.UTF8.GetBytes("doc2"));
        using var s3 = new MemoryStream(Encoding.UTF8.GetBytes("img1"));

        await _connector.WriteFileAsync("/docs/doc1.txt", s1);
        await _connector.WriteFileAsync("/docs/doc2.txt", s2);
        await _connector.WriteFileAsync("/images/img1.png", s3);

        var docsOnly = await _connector.ListFilesAsync("docs");

        docsOnly.Should().HaveCount(2);
        docsOnly.Should().AllSatisfy(f => f.Path.Should().StartWith("/docs/"));
    }

    [Fact]
    public async Task WriteAndListFilesAsync_MultipleFiles_AllReturned()
    {
        var fileNames = new[] { "alpha.txt", "beta.txt", "gamma.txt" };
        foreach (var name in fileNames)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"content of {name}"));
            await _connector.WriteFileAsync($"/{name}", stream);
        }

        var listed = await _connector.ListFilesAsync();

        listed.Should().HaveCount(3);
        listed.Select(f => f.Path).Should().BeEquivalentTo(fileNames.Select(n => $"/{n}"));
    }
}
