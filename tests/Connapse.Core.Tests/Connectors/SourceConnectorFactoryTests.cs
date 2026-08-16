using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

/// <summary>
/// A source splits what a container held in one connector_config blob across two rows: the
/// connection carries the credential and endpoint, the source carries the scope. The factory
/// has to recombine them into the config each connector already understands.
/// </summary>
[Trait("Category", "Unit")]
public class SourceConnectorFactoryTests
{
    private readonly IConnectorFactory _factory =
        new ConnectorFactory(Substitute.For<IManagedStorageProvider>());

    private static Connection MakeConnection(ConnectionProvider provider, string config, Guid? id = null) => new(
        Id: id ?? Guid.NewGuid(),
        Name: "conn",
        Provider: provider,
        ConfigJson: config,
        CreatedByUserId: null,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow);

    private static Source MakeSource(Guid connectionId, string scope) => new(
        Id: Guid.NewGuid(),
        Name: "src",
        Description: null,
        ConnectionId: connectionId,
        ScopeJson: scope,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow);

    [Fact]
    public void Create_S3Source_RecombinesCredentialAndScope()
    {
        // Deliberately no roleArn: S3Connector's constructor performs a blocking STS
        // AssumeRole call when one is set, which would make this a network test.
        var connection = MakeConnection(ConnectionProvider.S3, """{"region":"eu-west-1"}""");
        var source = MakeSource(connection.Id, """{"bucketName":"my-bucket","prefix":"docs/"}""");

        var connector = _factory.Create(source, connection);

        connector.Type.Should().Be(ConnectorType.S3);
    }

    [Fact]
    public void Create_AzureBlobSource_RecombinesCredentialAndScope()
    {
        var connection = MakeConnection(ConnectionProvider.AzureBlob, """{"storageAccountName":"acct","managedIdentityClientId":"mi-1"}""");
        var source = MakeSource(connection.Id, """{"containerName":"blobs","prefix":"p/"}""");

        var connector = _factory.Create(source, connection);

        connector.Type.Should().Be(ConnectorType.AzureBlob);
    }

    [Fact]
    public void Create_FilesystemSource_CombinesRootAndSubPath()
    {
        var connection = MakeConnection(ConnectionProvider.Filesystem, """{"allowedRoot":"/data"}""");
        var source = MakeSource(connection.Id, """{"subPath":"team","includePatterns":["*.md"]}""");

        var connector = _factory.Create(source, connection);

        connector.Type.Should().Be(ConnectorType.Filesystem);
    }

    [Fact]
    public void Create_SourceConnector_IsNeverWritable()
    {
        var connection = MakeConnection(ConnectionProvider.S3, """{"region":"eu-west-1"}""");
        var source = MakeSource(connection.Id, """{"bucketName":"b"}""");

        var connector = _factory.Create(source, connection);

        // The point of the whole epic: a source mirrors someone else's system and is never
        // mutated through Connapse.
        (connector is IWritableConnector).Should().BeFalse();
    }

    [Fact]
    public void Create_MismatchedConnection_Throws()
    {
        var connection = MakeConnection(ConnectionProvider.S3, """{"region":"eu-west-1"}""");
        var source = MakeSource(Guid.NewGuid(), """{"bucketName":"b"}""");

        Action act = () => _factory.Create(source, connection);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*does not own*");
    }

    [Fact]
    public void Create_FilesystemSubPathEscapingRoot_Throws()
    {
        // A source scope must not be able to reach outside the root its connection declares —
        // that root is the security boundary an admin configured.
        var connection = MakeConnection(ConnectionProvider.Filesystem, """{"allowedRoot":"/data"}""");
        var source = MakeSource(connection.Id, """{"subPath":"../etc"}""");

        Action act = () => _factory.Create(source, connection);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*outside*");
    }

    [Fact]
    public void Create_S3SourceWithNoBucket_Throws()
    {
        var connection = MakeConnection(ConnectionProvider.S3, """{"region":"eu-west-1"}""");
        var source = MakeSource(connection.Id, "{}");

        Action act = () => _factory.Create(source, connection);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void S3_RoleSessionName_IsWithinTheAwsLimitAndDoesNotThrow()
    {
        // Regression: this was $"connapse-{Guid:N}"[..64]. The literal is 41 characters, so
        // the slice threw ArgumentOutOfRangeException and assuming a role never worked —
        // every cross-account S3 connector crashed on construction.
        string name = S3Connector.BuildRoleSessionName();

        name.Length.Should().BeLessThanOrEqualTo(64);
        name.Should().StartWith("connapse-");
    }
}
