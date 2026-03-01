namespace Connapse.Storage.Connectors;

/// <summary>
/// Per-container configuration for the Azure Blob connector.
/// Stored as JSONB in containers.connector_config.
/// </summary>
public record AzureBlobConnectorConfig
{
    public string StorageAccountName { get; init; } = "";
    public string ContainerName { get; init; } = "";
    public string? Prefix { get; init; }
    public string? ManagedIdentityClientId { get; init; }
}
