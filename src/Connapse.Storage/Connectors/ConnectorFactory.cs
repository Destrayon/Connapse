using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;

namespace Connapse.Storage.Connectors;

/// <summary>
/// Creates IConnector instances from ContainerEntity configuration.
/// </summary>
public class ConnectorFactory : IConnectorFactory
{
    private readonly IManagedStorageProvider _managedStorageProvider;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ConnectorFactory(IManagedStorageProvider managedStorageProvider)
    {
        _managedStorageProvider = managedStorageProvider;
    }

    public IConnector Create(Container container)
    {
        return container.ConnectorType switch
        {
            ConnectorType.ManagedStorage => _managedStorageProvider.CreateConnector(container.Id),
            ConnectorType.Filesystem => CreateFilesystemConnector(container),
            ConnectorType.S3 => CreateS3Connector(container),
            ConnectorType.AzureBlob => CreateAzureBlobConnector(container),
            _ => throw new NotSupportedException($"Unknown connector type: {container.ConnectorType}")
        };
    }

    public IConnector Create(Source source, Connection connection)
    {
        if (source.ConnectionId != connection.Id)
            throw new ArgumentException(
                $"Connection '{connection.Id}' does not own source '{source.Id}'.", nameof(connection));

        using var credential = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(connection.ConfigJson) ? "{}" : connection.ConfigJson);
        using var scope = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(source.ScopeJson) ? "{}" : source.ScopeJson);

        return connection.Provider switch
        {
            ConnectionProvider.S3 => new S3Connector(new S3ConnectorConfig
            {
                Region = Str(credential, "region") ?? "us-east-1",
                RoleArn = Str(credential, "roleArn"),
                BucketName = Str(scope, "bucketName")
                    ?? throw new InvalidOperationException(
                        $"Source '{source.Name}' has no bucketName in its scope."),
                Prefix = Str(scope, "prefix"),
            }),

            ConnectionProvider.AzureBlob => new AzureBlobConnector(new AzureBlobConnectorConfig
            {
                StorageAccountName = Str(credential, "storageAccountName")
                    ?? throw new InvalidOperationException(
                        $"Connection '{connection.Name}' has no storageAccountName."),
                ManagedIdentityClientId = Str(credential, "managedIdentityClientId"),
                ContainerName = Str(scope, "containerName")
                    ?? throw new InvalidOperationException(
                        $"Source '{source.Name}' has no containerName in its scope."),
                Prefix = Str(scope, "prefix"),
            }),

            ConnectionProvider.Filesystem => new FilesystemConnector(new FilesystemConnectorConfig
            {
                RootPath = CombineUnderRoot(
                    Str(credential, "allowedRoot")
                        ?? throw new InvalidOperationException(
                            $"Connection '{connection.Name}' has no allowedRoot."),
                    Str(scope, "subPath"),
                    source.Name),
                IncludePatterns = Arr(scope, "includePatterns"),
                ExcludePatterns = Arr(scope, "excludePatterns"),
            }),

            _ => throw new NotSupportedException($"Unknown connection provider: {connection.Provider}")
        };
    }

    private static string? Str(JsonDocument doc, string name) =>
        doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static IReadOnlyList<string> Arr(JsonDocument doc, string name) =>
        doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray()
            : [];

    /// <summary>
    /// Resolves a source's subPath beneath its connection's allowed root, and verifies the
    /// result stays inside it. The allowed root is the boundary an admin configured, so a
    /// scope containing "../" must not be able to reach past it.
    /// </summary>
    private static string CombineUnderRoot(string allowedRoot, string? subPath, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(subPath))
            return Path.GetFullPath(allowedRoot);

        // Delegated rather than compared here (#365). The previous check was Path.GetFullPath
        // plus StartsWith, which is purely lexical: a junction inside the allowed root passed
        // it, and the connector then walked straight through to the target. PathConfinement
        // resolves links on every path segment before comparing.
        return PathConfinement.CombineWithin(allowedRoot, subPath)
            ?? throw new InvalidOperationException(
                $"Source '{sourceName}' resolves to a path outside its connection's allowed root '{allowedRoot}'.");
    }

    private static S3Connector CreateS3Connector(Container container)
    {
        if (string.IsNullOrEmpty(container.ConnectorConfig))
            throw new InvalidOperationException(
                $"S3 connector for container '{container.Name}' requires bucket configuration. No connector config found.");

        var config = JsonSerializer.Deserialize<S3ConnectorConfig>(container.ConnectorConfig, JsonOptions)
            ?? throw new InvalidOperationException(
                $"Failed to deserialize S3 connector config for container '{container.Name}'.");

        if (string.IsNullOrWhiteSpace(config.BucketName))
            throw new InvalidOperationException(
                $"S3 connector for container '{container.Name}' has an empty bucket name.");

        return new S3Connector(config);
    }

    private static AzureBlobConnector CreateAzureBlobConnector(Container container)
    {
        if (string.IsNullOrEmpty(container.ConnectorConfig))
            throw new InvalidOperationException(
                $"AzureBlob connector for container '{container.Name}' requires storage account configuration. No connector config found.");

        var config = JsonSerializer.Deserialize<AzureBlobConnectorConfig>(container.ConnectorConfig, JsonOptions)
            ?? throw new InvalidOperationException(
                $"Failed to deserialize AzureBlob connector config for container '{container.Name}'.");

        if (string.IsNullOrWhiteSpace(config.StorageAccountName))
            throw new InvalidOperationException(
                $"AzureBlob connector for container '{container.Name}' has an empty storage account name.");

        if (string.IsNullOrWhiteSpace(config.ContainerName))
            throw new InvalidOperationException(
                $"AzureBlob connector for container '{container.Name}' has an empty container name.");

        return new AzureBlobConnector(config);
    }

    private static FilesystemConnector CreateFilesystemConnector(Container container)
    {
        if (string.IsNullOrEmpty(container.ConnectorConfig))
            throw new InvalidOperationException(
                $"Filesystem connector for container '{container.Name}' requires a root path. No connector config found.");

        var config = JsonSerializer.Deserialize<FilesystemConnectorConfig>(container.ConnectorConfig, JsonOptions)
            ?? throw new InvalidOperationException(
                $"Failed to deserialize Filesystem connector config for container '{container.Name}'.");

        if (string.IsNullOrWhiteSpace(config.RootPath))
            throw new InvalidOperationException(
                $"Filesystem connector for container '{container.Name}' has an empty root path.");

        return new FilesystemConnector(config);
    }
}
