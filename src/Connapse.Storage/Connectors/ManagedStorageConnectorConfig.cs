namespace Connapse.Storage.Connectors;

/// <summary>
/// Per-container configuration for the Managed Storage connector.
/// Stored as JSONB in containers.connector_config.
/// Only stores the container/bucket name — the backing storage details
/// come from the platform-level IManagedStorageProvider.
/// </summary>
public record ManagedStorageConnectorConfig
{
    /// <summary>
    /// The blob container or bucket name (e.g., "org-a1b2c3d4-...").
    /// </summary>
    public string ContainerName { get; init; } = "";
}
