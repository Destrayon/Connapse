using System.Text.Json;
using Connapse.Storage.Connectors;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

[Trait("Category", "Unit")]
public class ConnectorConfigTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // --- S3ConnectorConfig ---

    [Fact]
    public void S3Config_Deserialize_AllFields()
    {
        var json = """{"bucketName":"my-bucket","region":"eu-west-1","prefix":"docs/","roleArn":"arn:aws:iam::123:role/Test"}""";
        var config = JsonSerializer.Deserialize<S3ConnectorConfig>(json, JsonOptions)!;
        config.BucketName.Should().Be("my-bucket");
        config.Region.Should().Be("eu-west-1");
        config.Prefix.Should().Be("docs/");
        config.RoleArn.Should().Be("arn:aws:iam::123:role/Test");
    }

    [Fact]
    public void S3Config_Deserialize_MinimalFields_UsesDefaults()
    {
        var json = """{"bucketName":"my-bucket"}""";
        var config = JsonSerializer.Deserialize<S3ConnectorConfig>(json, JsonOptions)!;
        config.BucketName.Should().Be("my-bucket");
        config.Region.Should().Be("us-east-1");
        config.Prefix.Should().BeNull();
        config.RoleArn.Should().BeNull();
    }

    [Fact]
    public void S3Config_Deserialize_EmptyJson_UsesDefaults()
    {
        var config = JsonSerializer.Deserialize<S3ConnectorConfig>("{}", JsonOptions)!;
        config.BucketName.Should().BeEmpty();
        config.Region.Should().Be("us-east-1");
        config.Prefix.Should().BeNull();
        config.RoleArn.Should().BeNull();
    }

    [Fact]
    public void S3Config_RoundTrip_PreservesValues()
    {
        var original = new S3ConnectorConfig
        {
            BucketName = "test",
            Region = "ap-southeast-1",
            Prefix = "data/",
            RoleArn = "arn:aws:iam::999:role/Reader"
        };
        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<S3ConnectorConfig>(json, JsonOptions)!;
        deserialized.Should().Be(original);
    }

    // --- AzureBlobConnectorConfig ---

    [Fact]
    public void AzureBlobConfig_Deserialize_AllFields()
    {
        var json = """{"storageAccountName":"acct","containerName":"docs","prefix":"wiki/","managedIdentityClientId":"abc-123"}""";
        var config = JsonSerializer.Deserialize<AzureBlobConnectorConfig>(json, JsonOptions)!;
        config.StorageAccountName.Should().Be("acct");
        config.ContainerName.Should().Be("docs");
        config.Prefix.Should().Be("wiki/");
        config.ManagedIdentityClientId.Should().Be("abc-123");
    }

    [Fact]
    public void AzureBlobConfig_Deserialize_MinimalFields_UsesDefaults()
    {
        var json = """{"storageAccountName":"acct","containerName":"docs"}""";
        var config = JsonSerializer.Deserialize<AzureBlobConnectorConfig>(json, JsonOptions)!;
        config.StorageAccountName.Should().Be("acct");
        config.ContainerName.Should().Be("docs");
        config.Prefix.Should().BeNull();
        config.ManagedIdentityClientId.Should().BeNull();
    }

    [Fact]
    public void AzureBlobConfig_Deserialize_EmptyJson_UsesDefaults()
    {
        var config = JsonSerializer.Deserialize<AzureBlobConnectorConfig>("{}", JsonOptions)!;
        config.StorageAccountName.Should().BeEmpty();
        config.ContainerName.Should().BeEmpty();
        config.Prefix.Should().BeNull();
        config.ManagedIdentityClientId.Should().BeNull();
    }

    [Fact]
    public void AzureBlobConfig_RoundTrip_PreservesValues()
    {
        var original = new AzureBlobConnectorConfig
        {
            StorageAccountName = "test",
            ContainerName = "blobs",
            Prefix = "data/",
            ManagedIdentityClientId = "client-id-123"
        };
        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AzureBlobConnectorConfig>(json, JsonOptions)!;
        deserialized.Should().Be(original);
    }
}
