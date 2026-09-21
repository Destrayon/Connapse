namespace Connapse.Storage.Connectors;

/// <summary>Recombined connection (account/endpoint) + source (container/prefix) config for the Azure Blob connector.</summary>
public record AzureBlobConnectorConfig
{
    public string AccountName { get; init; } = "";
    public string ContainerName { get; init; } = "";
    public string? Prefix { get; init; }
    /// <summary>Overrides https://{account}.blob.core.windows.net (Azurite/local).</summary>
    public string? BlobEndpoint { get; init; }
}
