namespace Connapse.Core.Interfaces;

/// <summary>
/// Abstracts the lifecycle and connector creation for platform-managed storage.
/// The backing implementation (MinIO, Azure Blob, S3) is swapped per deployment.
/// </summary>
public interface IManagedStorageProvider
{
    /// <summary>
    /// Creates the storage container/bucket for a knowledge container.
    /// Idempotent — does nothing if it already exists.
    /// </summary>
    Task CreateStorageAsync(string containerName, CancellationToken ct = default);

    /// <summary>
    /// Deletes the storage container/bucket and all its contents.
    /// </summary>
    Task DeleteStorageAsync(string containerName, CancellationToken ct = default);

    /// <summary>
    /// Creates an IConnector instance for reading/writing files in the given container.
    /// The provider maps the container ID to the appropriate storage location
    /// (e.g., path prefix in MinIO, blob container in Azure).
    /// </summary>
    IConnector CreateConnector(string containerId);
}
