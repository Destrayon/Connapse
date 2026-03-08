using System.Text;
using Azure.Storage.Blobs;
using FluentAssertions;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for AzureBlob connector behaviour against Azurite.
/// Uses TestableAzureBlobConnector (same path logic as AzureBlobConnector)
/// with an injected BlobContainerClient pointing at Azurite.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Azure Blob Connector Tests")]
public class AzureBlobConnectorIntegrationTests : IAsyncLifetime
{
    private readonly AzuriteFixture _azurite;
    private TestableAzureBlobConnector _connector = null!;
    private BlobContainerClient _containerClient = null!;
    private string _containerName = null!;

    public AzureBlobConnectorIntegrationTests(AzuriteFixture azurite)
    {
        _azurite = azurite;
    }

    public async Task InitializeAsync()
    {
        _containerName = $"connapse-test-{Guid.NewGuid():N}"[..32].ToLowerInvariant();
        _containerClient = await _azurite.CreateContainerAsync(_containerName);
        _connector = new TestableAzureBlobConnector(_containerClient);
    }

    public async Task DisposeAsync()
    {
        await _containerClient.DeleteIfExistsAsync();
    }

    [Fact]
    public async Task ListFilesAsync_EmptyContainer_ReturnsEmpty()
    {
        var files = await _connector.ListFilesAsync();

        files.Should().BeEmpty("no blobs have been uploaded to a fresh container");
    }

    [Fact]
    public async Task WriteAndListFilesAsync_SingleBlob_ReturnsBlob()
    {
        var content = Encoding.UTF8.GetBytes("Hello from Azure Blob connector test");
        using var stream = new MemoryStream(content);

        await _connector.WriteFileAsync("/docs/hello.txt", stream, "text/plain");
        var files = await _connector.ListFilesAsync();

        files.Should().HaveCount(1);
        files[0].Path.Should().Be("/docs/hello.txt");
        files[0].SizeBytes.Should().Be(content.Length);
        files[0].ContentType.Should().Be("text/plain");
    }

    [Fact]
    public async Task ReadFileAsync_ExistingBlob_ReturnsContent()
    {
        const string originalContent = "Azure Blob connector read test content";
        using var writeStream = new MemoryStream(Encoding.UTF8.GetBytes(originalContent));
        await _connector.WriteFileAsync("/data/readtest.txt", writeStream);

        using var readStream = await _connector.ReadFileAsync("/data/readtest.txt");
        using var reader = new StreamReader(readStream);
        var readContent = await reader.ReadToEndAsync();

        readContent.Should().Be(originalContent);
    }

    [Fact]
    public async Task ReadFileAsync_NonExistentBlob_ThrowsFileNotFoundException()
    {
        var act = async () => await _connector.ReadFileAsync("/does/not/exist.txt");

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task DeleteFileAsync_ExistingBlob_RemovesFromContainer()
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
    public async Task DeleteFileAsync_NonExistentBlob_DoesNotThrow()
    {
        var act = async () => await _connector.DeleteFileAsync("/ghost/file.txt");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ExistsAsync_ExistingBlob_ReturnsTrue()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("exists check"));
        await _connector.WriteFileAsync("/check/exists.txt", stream);

        var exists = await _connector.ExistsAsync("/check/exists.txt");

        exists.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_NonExistentBlob_ReturnsFalse()
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
    public async Task WriteAndListFilesAsync_MultipleBlobs_AllReturned()
    {
        var blobNames = new[] { "alpha.txt", "beta.txt", "gamma.txt" };
        foreach (var name in blobNames)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"content of {name}"));
            await _connector.WriteFileAsync($"/{name}", stream);
        }

        var listed = await _connector.ListFilesAsync();

        listed.Should().HaveCount(3);
        listed.Select(f => f.Path).Should().BeEquivalentTo(blobNames.Select(n => $"/{n}"));
    }

    [Fact]
    public async Task WriteAndListFilesAsync_WithConfigPrefix_PathsStrippedCorrectly()
    {
        var prefixedConnector = new TestableAzureBlobConnector(_containerClient, prefix: "tenant-a");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("prefixed content"));

        await prefixedConnector.WriteFileAsync("/report.txt", stream);
        var files = await prefixedConnector.ListFilesAsync();

        files.Should().HaveCount(1);
        files[0].Path.Should().Be("/report.txt",
            "the config prefix 'tenant-a/' should be stripped from the returned virtual path");
    }
}
