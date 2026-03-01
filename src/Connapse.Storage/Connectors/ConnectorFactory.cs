using System.Collections.Concurrent;
using System.Text.Json;
using Amazon.S3;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.FileSystem;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.Connectors;

/// <summary>
/// Creates IConnector instances from ContainerEntity configuration.
/// Registered as a Singleton so InMemoryConnector instances persist for the lifetime of the process.
/// </summary>
public class ConnectorFactory : IConnectorFactory
{
    private readonly IAmazonS3 _s3;
    private readonly IOptions<MinioOptions> _minioOptions;
    private readonly ConcurrentDictionary<Guid, InMemoryConnector> _inMemoryConnectors = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ConnectorFactory(IAmazonS3 s3, IOptions<MinioOptions> minioOptions)
    {
        _s3 = s3;
        _minioOptions = minioOptions;
    }

    public IConnector Create(Container container)
    {
        var containerId = Guid.Parse(container.Id);

        return container.ConnectorType switch
        {
            ConnectorType.MinIO => new MinioConnector(_s3, _minioOptions),
            ConnectorType.Filesystem => CreateFilesystemConnector(container),
            ConnectorType.InMemory => _inMemoryConnectors.GetOrAdd(containerId, _ => new InMemoryConnector()),
            ConnectorType.S3 => throw new NotImplementedException(
                "S3 connector will be implemented in Session C. Configure an AWS IAM role on the container."),
            ConnectorType.AzureBlob => throw new NotImplementedException(
                "AzureBlob connector will be implemented in Session D. Configure managed identity on the container."),
            _ => throw new NotSupportedException($"Unknown connector type: {container.ConnectorType}")
        };
    }

    /// <summary>
    /// Removes the InMemoryConnector instance for a container (used during ephemeral cleanup).
    /// </summary>
    public void EvictInMemoryConnector(Guid containerId)
        => _inMemoryConnectors.TryRemove(containerId, out _);

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
