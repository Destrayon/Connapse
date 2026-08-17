using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    private readonly IConnectorFactory _factory = BuildFactory(new SourceSecuritySettings());

    /// <summary>
    /// Builds a factory with the given source-security policy. Defaults to no configured
    /// allowlist, which is the unrestricted-with-a-warning path, so these tests keep covering
    /// recombination rather than the allowlist.
    /// </summary>
    private static ConnectorFactory BuildFactory(SourceSecuritySettings settings)
    {
        var monitor = Substitute.For<IOptionsMonitor<SourceSecuritySettings>>();
        monitor.CurrentValue.Returns(settings);

        return new ConnectorFactory(
            Substitute.For<IManagedStorageProvider>(),
            monitor,
            NullLogger<ConnectorFactory>.Instance);
    }

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

        var connector = (S3Connector)_factory.Create(source, connection);

        connector.Type.Should().Be(ConnectorType.S3);
        // Asserted field by field rather than on the type alone: a dropped region or a
        // mis-joined prefix still yields a connector of the right type, and then reads the
        // wrong bucket at runtime.
        connector.Config.Region.Should().Be("eu-west-1", "the region comes from the connection");
        connector.Config.BucketName.Should().Be("my-bucket", "the bucket comes from the source scope");
        connector.Config.Prefix.Should().Be("docs/");
    }

    [Fact]
    public void Create_AzureBlobSource_RecombinesCredentialAndScope()
    {
        var connection = MakeConnection(ConnectionProvider.AzureBlob, """{"storageAccountName":"acct","managedIdentityClientId":"mi-1"}""");
        var source = MakeSource(connection.Id, """{"containerName":"blobs","prefix":"p/"}""");

        var connector = (AzureBlobConnector)_factory.Create(source, connection);

        connector.Type.Should().Be(ConnectorType.AzureBlob);

        // This is the only coverage of the Azure recombination. Unlike S3 — which LocalStack
        // serves, so SourceSyncS3IntegrationTests exercises the mapping against a live
        // remote — Azurite cannot authenticate DefaultAzureCredential, and redirecting the
        // connector to it would mean supporting shared-key auth: precisely the stored cloud
        // credential this project does not accept.
        connector.Config.StorageAccountName.Should().Be("acct", "the account comes from the connection");
        connector.Config.ManagedIdentityClientId.Should().Be("mi-1",
            "dropping this silently falls back to the default identity, which may have wider access");
        connector.Config.ContainerName.Should().Be("blobs", "the container comes from the source scope");
        connector.Config.Prefix.Should().Be("p/");
    }

    [Fact]
    public void Create_FilesystemSource_CombinesRootAndSubPath()
    {
        var connection = MakeConnection(ConnectionProvider.Filesystem, """{"allowedRoot":"/data"}""");
        var source = MakeSource(connection.Id, """{"subPath":"team","includePatterns":["*.md"]}""");

        var connector = (FilesystemConnector)_factory.Create(source, connection);

        connector.Type.Should().Be(ConnectorType.Filesystem);
        connector.Config.RootPath.Should().Be(
            Path.GetFullPath(Path.Combine("/data", "team")),
            "the source is confined to its subPath beneath the connection's root");
        connector.Config.IncludePatterns.Should().BeEquivalentTo(["*.md"]);
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
    public void Create_FilesystemRootOutsideTheAllowlist_Throws()
    {
        // The allowlist bounds what the root itself may be. Link resolution bounds where a
        // source may reach from there. Neither substitutes for the other: this is the control
        // that stops allowedRoot: "/".
        var factory = BuildFactory(new SourceSecuritySettings
        {
            AllowedFilesystemRoots = [Path.Combine(Path.GetTempPath(), "connapse-permitted")]
        });

        var connection = MakeConnection(ConnectionProvider.Filesystem, """{"allowedRoot":"/"}""");
        var source = MakeSource(connection.Id, "{}");

        Action act = () => factory.Create(source, connection);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AllowedFilesystemRoots*");
    }

    [Fact]
    public void Create_FilesystemRootInsideTheAllowlist_IsAccepted()
    {
        string permitted = Path.Combine(Path.GetTempPath(), $"connapse-allow-{Guid.NewGuid():N}");
        string root = Path.Combine(permitted, "team");
        Directory.CreateDirectory(root);

        try
        {
            var factory = BuildFactory(new SourceSecuritySettings { AllowedFilesystemRoots = [permitted] });

            // A root nested inside a permitted entry is allowed — operators configure the
            // parent once rather than enumerating every source directory.
            var connection = MakeConnection(ConnectionProvider.Filesystem, $$"""{"allowedRoot":{{JsonSerializer.Serialize(root)}}}""");
            var source = MakeSource(connection.Id, "{}");

            factory.Create(source, connection).Type.Should().Be(ConnectorType.Filesystem);
        }
        finally
        {
            Directory.Delete(permitted, recursive: true);
        }
    }

    [Fact]
    public void Create_FilesystemRootWithNoAllowlistConfigured_IsAcceptedForNow()
    {
        // Deliberately permissive for one release: #350 backfilled existing filesystem
        // containers into connections, so enforcing immediately would break every upgrade
        // until an operator edited configuration. The factory logs a warning naming the root.
        var factory = BuildFactory(new SourceSecuritySettings());

        var connection = MakeConnection(ConnectionProvider.Filesystem, """{"allowedRoot":"/data"}""");
        var source = MakeSource(connection.Id, "{}");

        factory.Create(source, connection).Type.Should().Be(ConnectorType.Filesystem);
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
