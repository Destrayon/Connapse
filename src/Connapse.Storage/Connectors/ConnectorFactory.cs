using System.Text.Json;
using Amazon.S3;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.FileSystem;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.Connectors;

/// <summary>
/// Creates IConnector instances from ContainerEntity configuration.
/// </summary>
public class ConnectorFactory : IConnectorFactory
{
    private readonly IAmazonS3 _s3;
    private readonly IOptions<MinioOptions> _minioOptions;

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
        return container.ConnectorType switch
        {
            ConnectorType.MinIO => new MinioConnector(_s3, _minioOptions),
            ConnectorType.Filesystem => CreateFilesystemConnector(container),
            ConnectorType.S3 => CreateS3Connector(container),
            ConnectorType.AzureBlob => CreateAzureBlobConnector(container),
            _ => throw new NotSupportedException($"Unknown connector type: {container.ConnectorType}")
        };
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
