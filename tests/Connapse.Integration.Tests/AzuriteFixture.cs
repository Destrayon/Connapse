using Azure.Storage.Blobs;
using Testcontainers.Azurite;

namespace Connapse.Integration.Tests;

/// <summary>
/// Shared fixture that starts an Azurite container for Azure Blob Storage testing.
/// Provides a BlobServiceClient using Azurite's well-known development connection string.
/// </summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    private readonly AzuriteContainer _container = new AzuriteBuilder()
        .WithImage("mcr.microsoft.com/azure-storage/azurite:3.33.0")
        .WithCommand("--skipApiVersionCheck")
        .Build();

    public BlobServiceClient BlobServiceClient { get; private set; } = null!;
    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        BlobServiceClient = new BlobServiceClient(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    public async Task<BlobContainerClient> CreateContainerAsync(string name, CancellationToken ct = default)
    {
        var client = BlobServiceClient.GetBlobContainerClient(name);
        await client.CreateIfNotExistsAsync(cancellationToken: ct);
        return client;
    }
}
