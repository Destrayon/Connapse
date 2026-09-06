using Azure.Storage.Blobs;
using Connapse.Storage.Connectors;
using FluentAssertions;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for AzureBlobConnector against Azurite. Azurite cannot authenticate an
/// AAD TokenCredential, so the connector is constructed via its internal ctor with a
/// shared-key BlobServiceClient pointed at the emulator.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AzureBlobConnector")]
public class AzureBlobConnectorIntegrationTests(AzuriteFixture fixture)
{
    [Fact]
    public async Task ListAndRead_ScopedToPrefix()
    {
        var service = new BlobServiceClient(fixture.ConnectionString); // shared-key, Azurite
        var container = service.GetBlobContainerClient("docs");
        await container.CreateIfNotExistsAsync();
        await container.UploadBlobAsync("reports/q1.pdf", new BinaryData("hello"));
        await container.UploadBlobAsync("other/skip.txt", new BinaryData("nope"));

        var connector = new AzureBlobConnector(
            new AzureBlobConnectorConfig { AccountName = "devstoreaccount1", ContainerName = "docs", Prefix = "reports/" },
            service); // internal test ctor

        var files = await connector.ListFilesAsync();
        files.Should().ContainSingle();
        files[0].Path.Should().Be("reports/q1.pdf");
        files[0].ResourceUri.Should().Be("azblob://devstoreaccount1/docs/reports/q1.pdf");

        (await connector.ExistsAsync("reports/q1.pdf")).Should().BeTrue();
        using var stream = await connector.ReadFileAsync("reports/q1.pdf");
        (await new StreamReader(stream).ReadToEndAsync()).Should().Be("hello");
    }
}
