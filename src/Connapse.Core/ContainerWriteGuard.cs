using System.Text.Json;

namespace Connapse.Core;

/// <summary>
/// Centralized write-permission checks for containers.
/// Used by both REST API endpoints and MCP tools to enforce read-only
/// connector types and Filesystem permission flags consistently.
/// </summary>
public static class ContainerWriteGuard
{
    /// <summary>
    /// Returns an error message if the container does not allow the given write operation,
    /// or null if the operation is permitted.
    /// </summary>
    public static string? CheckWrite(Container container, WriteOperation operation)
    {
        // S3 and AzureBlob containers are always read-only (synced from source)
        if (container.ConnectorType is ConnectorType.S3 or ConnectorType.AzureBlob)
            return $"{container.ConnectorType} containers are read-only. Files are synced from the source.";

        // Filesystem containers respect per-container permission flags
        if (container.ConnectorType == ConnectorType.Filesystem
            && !string.IsNullOrEmpty(container.ConnectorConfig))
        {
            var config = JsonSerializer.Deserialize<FilesystemPermissions>(
                container.ConnectorConfig,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (config is not null)
            {
                var denied = operation switch
                {
                    WriteOperation.Upload => !config.AllowUpload,
                    WriteOperation.Delete => !config.AllowDelete,
                    WriteOperation.CreateFolder => !config.AllowCreateFolder,
                    _ => false
                };

                if (denied)
                    return $"This Filesystem container does not allow {operation.ToString().ToLowerInvariant()} operations.";
            }
        }

        return null;
    }

    /// <summary>
    /// Convenience: returns true if the container is fully read-only (S3/AzureBlob).
    /// </summary>
    public static bool IsReadOnly(ConnectorType type) =>
        type is ConnectorType.S3 or ConnectorType.AzureBlob;

    /// <summary>
    /// Minimal DTO to deserialize only the permission flags from FilesystemConnectorConfig,
    /// avoiding a dependency on Connapse.Storage.
    /// </summary>
    private record FilesystemPermissions
    {
        public bool AllowDelete { get; init; } = true;
        public bool AllowUpload { get; init; } = true;
        public bool AllowCreateFolder { get; init; } = true;
    }
}

public enum WriteOperation
{
    Upload,
    Delete,
    CreateFolder
}
