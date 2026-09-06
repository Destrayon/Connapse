using Testcontainers.Azurite;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Shared fixture that starts an Azurite container for Azure Blob connector testing.
/// Azurite cannot authenticate an AAD TokenCredential, so tests drive the connector
/// through its internal ctor with a shared-key BlobServiceClient built from
/// <see cref="ConnectionString"/>.
/// </summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    // Testcontainers.Azurite 4.3.0's default image (3.28.0) rejects the x-ms-version header
    // sent by Azure.Storage.Blobs 12.24.0 (the version Connapse.Storage references) with
    // "The API version 2025-05-05 is not supported by Azurite" on every request. Pin to
    // :latest so the emulator's supported API surface keeps pace with the client SDK.
    private readonly AzuriteContainer _container = new AzuriteBuilder()
        .WithImage("mcr.microsoft.com/azure-storage/azurite:latest")
        .Build();
    public string ConnectionString => _container.GetConnectionString();
    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
