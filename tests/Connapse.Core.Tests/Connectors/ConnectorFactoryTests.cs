using System.Text.Json;
using Amazon.S3;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors;
using Connapse.Storage.FileSystem;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

[Trait("Category", "Unit")]
public class ConnectorFactoryTests
{
    private readonly ConnectorFactory _factory;

    public ConnectorFactoryTests()
    {
        var managedStorageProvider = Substitute.For<IManagedStorageProvider>();
        managedStorageProvider.CreateConnector(Arg.Any<string>())
            .Returns(ci => Substitute.For<IConnector>());
        _factory = new ConnectorFactory(managedStorageProvider);
    }

    [Fact]
    public void Create_MinIO_ReturnsConnectorFromProvider()
    {
        var container = MakeContainer(ConnectorType.ManagedStorage);
        var connector = _factory.Create(container);
        connector.Should().NotBeNull();
    }

    [Fact]
    public void Create_Filesystem_ValidConfig_ReturnsFilesystemConnector()
    {
        var config = JsonSerializer.Serialize(new { rootPath = "C:\\temp\\test" });
        var container = MakeContainer(ConnectorType.Filesystem, config);
        var connector = _factory.Create(container);
        connector.Type.Should().Be(ConnectorType.Filesystem);
        connector.SupportsLiveWatch.Should().BeTrue();
    }

    [Fact]
    public void Create_Filesystem_MissingConfig_Throws()
    {
        var container = MakeContainer(ConnectorType.Filesystem);
        var act = () => _factory.Create(container);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*root path*");
    }

    [Fact]
    public void Create_S3_ValidConfig_ReturnsS3Connector()
    {
        var config = JsonSerializer.Serialize(new { bucketName = "test-bucket", region = "us-west-2" });
        var container = MakeContainer(ConnectorType.S3, config);
        var connector = _factory.Create(container);
        connector.Type.Should().Be(ConnectorType.S3);
        connector.SupportsLiveWatch.Should().BeFalse();
    }

    [Fact]
    public void Create_S3_MissingConfig_Throws()
    {
        var container = MakeContainer(ConnectorType.S3);
        var act = () => _factory.Create(container);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*bucket configuration*");
    }

    [Fact]
    public void Create_S3_EmptyBucketName_Throws()
    {
        var config = JsonSerializer.Serialize(new { bucketName = "", region = "us-east-1" });
        var container = MakeContainer(ConnectorType.S3, config);
        var act = () => _factory.Create(container);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*empty bucket name*");
    }

    [Fact]
    public void Create_AzureBlob_ValidConfig_ReturnsAzureBlobConnector()
    {
        var config = JsonSerializer.Serialize(new { storageAccountName = "myaccount", containerName = "docs" });
        var container = MakeContainer(ConnectorType.AzureBlob, config);
        var connector = _factory.Create(container);
        connector.Type.Should().Be(ConnectorType.AzureBlob);
        connector.SupportsLiveWatch.Should().BeFalse();
    }

    [Fact]
    public void Create_AzureBlob_MissingConfig_Throws()
    {
        var container = MakeContainer(ConnectorType.AzureBlob);
        var act = () => _factory.Create(container);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*storage account configuration*");
    }

    [Fact]
    public void Create_AzureBlob_EmptyStorageAccountName_Throws()
    {
        var config = JsonSerializer.Serialize(new { storageAccountName = "", containerName = "docs" });
        var container = MakeContainer(ConnectorType.AzureBlob, config);
        var act = () => _factory.Create(container);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*empty storage account name*");
    }

    [Fact]
    public void Create_AzureBlob_EmptyContainerName_Throws()
    {
        var config = JsonSerializer.Serialize(new { storageAccountName = "myaccount", containerName = "" });
        var container = MakeContainer(ConnectorType.AzureBlob, config);
        var act = () => _factory.Create(container);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*empty container name*");
    }

    [Fact]
    public void Create_S3_WithPrefix_Succeeds()
    {
        var config = JsonSerializer.Serialize(new { bucketName = "test-bucket", region = "eu-west-1", prefix = "documents/" });
        var container = MakeContainer(ConnectorType.S3, config);
        var connector = _factory.Create(container);
        connector.Type.Should().Be(ConnectorType.S3);
    }

    [Fact]
    public void Create_AzureBlob_WithAllFields_Succeeds()
    {
        var config = JsonSerializer.Serialize(new
        {
            storageAccountName = "myaccount",
            containerName = "docs",
            prefix = "team/",
            managedIdentityClientId = "00000000-0000-0000-0000-000000000000"
        });
        var container = MakeContainer(ConnectorType.AzureBlob, config);
        var connector = _factory.Create(container);
        connector.Type.Should().Be(ConnectorType.AzureBlob);
    }

    private static Container MakeContainer(ConnectorType type, string? connectorConfig = null) =>
        new(
            Id: Guid.NewGuid().ToString(),
            Name: "test",
            Description: null,
            ConnectorType: type,
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            ConnectorConfig: connectorConfig);
}
