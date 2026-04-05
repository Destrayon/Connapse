namespace Connapse.Core.Interfaces;

/// <summary>
/// Abstracts the lifecycle and connector creation for platform-managed storage.
/// The backing implementation (MinIO, Azure Blob, S3) is swapped per deployment.
/// </summary>
public interface IManagedStorageProvider
{
    /// <summary>
    /// Creates the storage container/bucket for a tenant.
    /// Idempotent — does nothing if it already exists.
    /// </summary>
    Task CreateStorageAsync(string containerName, CancellationToken ct = default);

    /// <summary>
    /// Deletes the storage container/bucket and all its contents.
    /// </summary>
    Task DeleteStorageAsync(string containerName, CancellationToken ct = default);

    /// <summary>
    /// Creates an IConnector instance for reading/writing to the given managed container.
    /// </summary>
    IConnector CreateConnector(string containerName);
}
