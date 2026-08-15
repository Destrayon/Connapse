using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Backfill;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Backfill;

[Trait("Category", "Unit")]
public class ConnectorConfigMapperTests
{
    private readonly IConnectorConfigMapper _mapper = new ConnectorConfigMapper();

    [Fact]
    public void Map_S3_SplitsCredentialFromScope()
    {
        var config = """{"bucketName":"docs","region":"us-east-1","prefix":"team/","roleArn":"arn:aws:iam::1:role/r"}""";

        var (connection, scopeJson) = _mapper.Map(ConnectorType.S3, config, "docs-container");

        connection.Provider.Should().Be(ConnectionProvider.S3);
        connection.ConfigJson.Should().Contain("us-east-1").And.Contain("arn:aws:iam::1:role/r");
        // The bucket is scope, not credential — two buckets in one region share a connection.
        connection.ConfigJson.Should().NotContain("docs");
        scopeJson.Should().Contain("docs").And.Contain("team/");
    }

    [Fact]
    public void Map_S3_SameRegionAndRole_ProducesSameDedupKey()
    {
        var a = """{"bucketName":"one","region":"us-east-1","roleArn":"arn:r"}""";
        var b = """{"bucketName":"two","region":"us-east-1","roleArn":"arn:r"}""";

        _mapper.Map(ConnectorType.S3, a, "a").Connection.DedupKey
            .Should().Be(_mapper.Map(ConnectorType.S3, b, "b").Connection.DedupKey);
    }

    [Fact]
    public void Map_S3_SameRegionAndRole_ProducesSameConnectionName()
    {
        // The name is what actually deduplicates against the unique index, so it must
        // agree with the dedup key.
        var a = """{"bucketName":"one","region":"us-east-1","roleArn":"arn:r"}""";
        var b = """{"bucketName":"two","region":"us-east-1","roleArn":"arn:r"}""";

        _mapper.Map(ConnectorType.S3, a, "a").Connection.Name
            .Should().Be(_mapper.Map(ConnectorType.S3, b, "b").Connection.Name);
    }

    [Fact]
    public void Map_S3_DifferentRegion_ProducesDifferentDedupKeyAndName()
    {
        var a = """{"bucketName":"one","region":"us-east-1"}""";
        var b = """{"bucketName":"one","region":"eu-west-1"}""";

        var first = _mapper.Map(ConnectorType.S3, a, "a").Connection;
        var second = _mapper.Map(ConnectorType.S3, b, "b").Connection;

        first.DedupKey.Should().NotBe(second.DedupKey);
        first.Name.Should().NotBe(second.Name);
    }

    [Fact]
    public void Map_AzureBlob_SplitsAccountFromContainerName()
    {
        var config = """{"storageAccountName":"acct","containerName":"blobs","prefix":"p/","managedIdentityClientId":"mi-1"}""";

        var (connection, scopeJson) = _mapper.Map(ConnectorType.AzureBlob, config, "azure-container");

        connection.Provider.Should().Be(ConnectionProvider.AzureBlob);
        connection.ConfigJson.Should().Contain("acct").And.Contain("mi-1");
        scopeJson.Should().Contain("blobs").And.Contain("p/");
    }

    [Fact]
    public void Map_Filesystem_RootIsCredentialScopeIsPatterns()
    {
        var config = """{"rootPath":"/data","includePatterns":["*.md"],"excludePatterns":["*.tmp"]}""";

        var (connection, scopeJson) = _mapper.Map(ConnectorType.Filesystem, config, "fs-container");

        connection.Provider.Should().Be(ConnectionProvider.Filesystem);
        connection.ConfigJson.Should().Contain("/data");
        scopeJson.Should().Contain("*.md").And.Contain("*.tmp");
    }

    [Fact]
    public void Map_NullConfig_DoesNotThrow()
    {
        var (connection, scopeJson) = _mapper.Map(ConnectorType.S3, null, "bare");

        connection.Provider.Should().Be(ConnectionProvider.S3);
        scopeJson.Should().NotBeNull();
    }

    [Fact]
    public void Map_ManagedStorage_Throws()
    {
        // Managed storage is never migrated — it is Connapse's own backend.
        Action act = () => _mapper.Map(ConnectorType.ManagedStorage, null, "managed");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Map_ConnectionName_IsWithinDatabaseLimit()
    {
        var longRoot = "/" + new string('x', 400);
        var config = $$"""{"rootPath":"{{longRoot}}"}""";

        var (connection, _) = _mapper.Map(ConnectorType.Filesystem, config, "fs");

        // connections.name is varchar(128).
        connection.Name.Length.Should().BeLessThanOrEqualTo(128);
    }
}
