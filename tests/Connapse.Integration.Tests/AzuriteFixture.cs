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
    // "The API version 2025-05-05 is not supported by Azurite" on every request. Pin to a
    // concrete newer version (not :latest, which floats) whose supported API surface keeps
    // pace with the client SDK. 3.37.0 is confirmed via `docker manifest inspect` to be the
    // exact version :latest resolved to at the time this was verified working
    // (manifest-list digest sha256:830430c1da1a2d537e08f3e6764dd1f5ae00cf0346bcaf625b968ec3f0971fd5).
    private readonly AzuriteContainer _container = new AzuriteBuilder()
        .WithImage("mcr.microsoft.com/azure-storage/azurite:3.37.0")
        .Build();
    public string ConnectionString => _container.GetConnectionString();
    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
