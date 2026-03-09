namespace Connapse.Storage.Connectors;

/// <summary>
/// Per-container configuration for the MinIO connector.
/// Stored as JSONB in containers.connector_config.
/// </summary>
public record MinioConnectorConfig
{
    /// <summary>
    /// The container ID, used as the S3 key prefix to isolate each container's files.
    /// Set by ConnectorFactory — not persisted in JSON.
    /// </summary>
    public string ContainerId { get; init; } = "";
}
