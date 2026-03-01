namespace Connapse.Storage.Connectors;

/// <summary>
/// Per-container configuration for the S3 connector.
/// Stored as JSONB in containers.connector_config.
/// </summary>
public record S3ConnectorConfig
{
    public string BucketName { get; init; } = "";
    public string Region { get; init; } = "us-east-1";
    public string? Prefix { get; init; }
    public string? RoleArn { get; init; }
}
