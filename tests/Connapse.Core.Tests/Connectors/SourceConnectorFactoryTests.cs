using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Connapse.Storage.CloudScope;
using Microsoft.Extensions.DependencyInjection;
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
    /// Captures log entries so the grace-path warning can be asserted. Written by hand rather
    /// than substituted because ILogger.Log is generic, which makes the mock-based assertion
    /// far harder to read than the thing it is checking.
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        public IEnumerable<string> Warnings =>
            Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);
    }

    /// <summary>
    /// Builds a factory with the given source-security policy. Defaults to no configured
    /// allowlist, which is the unrestricted-with-a-warning path, so these tests keep covering
    /// recombination rather than the allowlist.
    /// </summary>
    private static ConnectorFactory BuildFactory(
        SourceSecuritySettings settings,
        ILogger<ConnectorFactory>? logger = null,
        ISshHostKeyStore? hostKeyStore = null)
    {
        var monitor = Substitute.For<IOptionsMonitor<SourceSecuritySettings>>();
        monitor.CurrentValue.Returns(settings);

        // A credential provider over an empty container: nothing is configured, so it falls back to
        // the SDK chain exactly as an unconfigured deployment does. These tests are about scope and
        // allowlist rules, and none of them reaches AWS.
        var credentialStore = Substitute.For<IProviderCredentialStore>();
        credentialStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ProviderCredentialInfo?)null);

        var services = new ServiceCollection();
        services.AddSingleton(credentialStore);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new ConnectorFactory(
            monitor,
            hostKeyStore ?? Substitute.For<ISshHostKeyStore>(),
            new ConnapseAwsCredentials(scopeFactory, NullLogger<ConnapseAwsCredentials>.Instance),
            logger ?? NullLogger<ConnectorFactory>.Instance);
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
    public void Create_S3SourceNamingABucketOutsideAllowedLocations_Throws()
    {
        // The connection's IAM role can read both buckets — this allowlist is the only thing
        // distinguishing them, because every source on the connection is the same AWS principal.
        var connection = MakeConnection(ConnectionProvider.S3,
            """{"region":"eu-west-1","allowedLocations":["docs-bucket"]}""");
        var source = MakeSource(connection.Id, """{"bucketName":"payroll-bucket"}""");

        Action act = () => _factory.Create(source, connection);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*allowedLocations*");
    }

    [Fact]
    public void Create_S3SourceInsideAllowedLocations_IsAccepted()
    {
        var connection = MakeConnection(ConnectionProvider.S3,
            """{"region":"eu-west-1","allowedLocations":["docs-bucket/team"]}""");
        var source = MakeSource(connection.Id, """{"bucketName":"docs-bucket","prefix":"team/2026/"}""");

        var connector = (S3Connector)_factory.Create(source, connection);

        connector.Config.BucketName.Should().Be("docs-bucket");
    }

    [Fact]
    public void Create_AzureSourceNamingAContainerOutsideAllowedLocations_Throws()
    {
        var connection = MakeConnection(ConnectionProvider.AzureBlob,
            """{"storageAccountName":"acct","allowedLocations":["public-docs"]}""");
        var source = MakeSource(connection.Id, """{"containerName":"hr-private"}""");

        Action act = () => _factory.Create(source, connection);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*allowedLocations*");
    }

    [Fact]
    public void Create_CloudSourceWithNoAllowedLocations_IsAcceptedAndWarns()
    {
        // Same one-release grace as the filesystem root allowlist: #350 backfilled existing
        // cloud containers into connections that declare no locations. The warning is
        // asserted because it is the only signal an operator gets before this becomes
        // deny-by-default — silently permitting would be indistinguishable from working.
        var logger = new RecordingLogger<ConnectorFactory>();
        var factory = BuildFactory(new SourceSecuritySettings(), logger);

        var connection = MakeConnection(ConnectionProvider.S3, """{"region":"eu-west-1"}""");
        var source = MakeSource(connection.Id, """{"bucketName":"anything"}""");

        factory.Create(source, connection).Type.Should().Be(ConnectorType.S3);

        logger.Warnings.Should().ContainSingle()
            .Which.Should().Contain("anything", "the warning must name the container so an operator knows what to allowlist");
    }

    [Fact]
    public void Create_RepeatedCyclesOverTheSameScope_WarnOnlyOnce()
    {
        // A connector is built every sync cycle. Warning each time would emit one line per
        // source every five minutes and bury the message it exists to deliver.
        var logger = new RecordingLogger<ConnectorFactory>();
        var factory = BuildFactory(new SourceSecuritySettings(), logger);

        var connection = MakeConnection(ConnectionProvider.S3, """{"region":"eu-west-1"}""");
        var source = MakeSource(connection.Id, """{"bucketName":"anything"}""");

        factory.Create(source, connection);
        factory.Create(source, connection);
        factory.Create(source, connection);

        logger.Warnings.Should().ContainSingle();
    }

    [Fact]
    public void Create_DifferentScopesOnTheSameConnection_EachWarn()
    {
        // Deduplication must not hide a second unrestricted bucket behind the first.
        var logger = new RecordingLogger<ConnectorFactory>();
        var factory = BuildFactory(new SourceSecuritySettings(), logger);

        var connection = MakeConnection(ConnectionProvider.S3, """{"region":"eu-west-1"}""");
        factory.Create(MakeSource(connection.Id, """{"bucketName":"first"}"""), connection);
        factory.Create(MakeSource(connection.Id, """{"bucketName":"second"}"""), connection);

        logger.Warnings.Should().HaveCount(2);
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
    public void Create_FilesystemRootWithNoAllowlistConfigured_IsAcceptedAndWarns()
    {
        // Deliberately permissive for one release: #350 backfilled existing filesystem
        // containers into connections, so enforcing immediately would break every upgrade
        // until an operator edited configuration.
        var logger = new RecordingLogger<ConnectorFactory>();
        var factory = BuildFactory(new SourceSecuritySettings(), logger);

        var connection = MakeConnection(ConnectionProvider.Filesystem, """{"allowedRoot":"/data"}""");
        var source = MakeSource(connection.Id, "{}");

        factory.Create(source, connection).Type.Should().Be(ConnectorType.Filesystem);

        logger.Warnings.Should().ContainSingle()
            .Which.Should().Contain("AllowedFilesystemRoots",
                "the warning must name the setting an operator has to configure");
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
    // ── Malformed allowlists must not fail open ────────────────────────────

    /// <summary>
    /// The enforcement point, which is what was actually broken. The create-time preflight
    /// refused these, but the factory's reader filtered non-strings out — so a malformed
    /// allowlist shrank to an empty one, empty read as "declared nothing", and the grace path
    /// let the source name any bucket the shared credential could reach.
    /// </summary>
    /// <remarks>
    /// Tested here rather than only in the preflight because POST /api/sources does not run the
    /// preflight at all. For an API-created source the factory is the only check there is.
    /// </remarks>
    [Theory]
    [InlineData("""{"region":"eu-west-1","allowedLocations":[42]}""")]
    [InlineData("""{"region":"eu-west-1","allowedLocations":[null]}""")]
    [InlineData("""{"region":"eu-west-1","allowedLocations":[{"bucket":"b"}]}""")]
    [InlineData("""{"region":"eu-west-1","allowedLocations":[[ "b" ]]}""")]
    [InlineData("""{"region":"eu-west-1","allowedLocations":[42,"other-bucket"]}""")]
    public void Create_S3MalformedAllowedLocations_IsRefused(string config)
    {
        var connection = MakeConnection(ConnectionProvider.S3, config);
        var source = MakeSource(connection.Id, """{"bucketName":"anything"}""");

        Action act = () => _factory.Create(source, connection);

        act.Should().Throw<InvalidOperationException>(
            "a declared-but-broken allowlist must never read as an absent one");
    }

    /// <summary>
    /// A value that is present but not an array at all. Previously the reader's array check
    /// failed and it returned empty — the same collapse by a different route.
    /// </summary>
    [Theory]
    [InlineData("""{"region":"eu-west-1","allowedLocations":"my-bucket"}""")]
    [InlineData("""{"region":"eu-west-1","allowedLocations":42}""")]
    [InlineData("""{"region":"eu-west-1","allowedLocations":{"0":"b"}}""")]
    public void Create_AllowedLocationsThatIsNotAnArray_IsRefused(string config)
    {
        var connection = MakeConnection(ConnectionProvider.S3, config);
        var source = MakeSource(connection.Id, """{"bucketName":"my-bucket"}""");

        Action act = () => _factory.Create(source, connection);

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// An explicitly empty allowlist declares a control that permits nothing, which is not the
    /// same as declaring none. The connection form omits the property entirely when blank for
    /// exactly this reason.
    /// </summary>
    [Fact]
    public void Create_ExplicitlyEmptyAllowedLocations_IsRefused()
    {
        var connection = MakeConnection(ConnectionProvider.S3, """{"region":"eu-west-1","allowedLocations":[]}""");
        var source = MakeSource(connection.Id, """{"bucketName":"b"}""");

        Action act = () => _factory.Create(source, connection);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Create_AzureMalformedAllowedLocations_IsRefused()
    {
        var connection = MakeConnection(ConnectionProvider.AzureBlob,
            """{"storageAccountName":"acct","allowedLocations":[42]}""");
        var source = MakeSource(connection.Id, """{"containerName":"anything"}""");

        Action act = () => _factory.Create(source, connection);

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// The grace path has to survive all of this: #350 backfilled connections that declare no
    /// allowlist, and refusing those would break every upgrade.
    /// </summary>
    [Fact]
    public void Create_NoAllowedLocationsDeclared_StillTakesTheGracePath()
    {
        var connection = MakeConnection(ConnectionProvider.S3, """{"region":"eu-west-1"}""");
        var source = MakeSource(connection.Id, """{"bucketName":"anything"}""");

        _factory.Create(source, connection).Type.Should().Be(ConnectorType.S3);
    }

    [Fact]
    public void Create_WellFormedAllowedLocations_StillPermitTheirBucket()
    {
        var connection = MakeConnection(ConnectionProvider.S3,
            """{"region":"eu-west-1","allowedLocations":["mine","other/docs"]}""");
        var source = MakeSource(connection.Id, """{"bucketName":"mine"}""");

        _factory.Create(source, connection).Type.Should().Be(ConnectorType.S3);
    }
    /// <summary>
    /// A configuration that parses but is not an object. It reached an exception before this
    /// guard too, but only because JsonElement.TryGetProperty throws on a non-object — an
    /// accident of which field the object initialiser happened to read first. The message now
    /// says what is wrong, and reordering cannot lose the check.
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("""["my-bucket"]""")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    public void Create_ConnectionConfigThatIsNotAnObject_IsRefused(string config)
    {
        var connection = MakeConnection(ConnectionProvider.S3, config);
        var source = MakeSource(connection.Id, """{"bucketName":"anything"}""");

        Action act = () => _factory.Create(source, connection);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not a JSON object*");
    }

    [Fact]
    public void Create_SourceScopeThatIsNotAnObject_IsRefused()
    {
        var connection = MakeConnection(ConnectionProvider.S3, """{"region":"eu-west-1"}""");
        var source = MakeSource(connection.Id, "[]");

        Action act = () => _factory.Create(source, connection);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not a JSON object*");
    }

    // ── SFTP ───────────────────────────────────────────────────────────────

    private const string SftpConfig =
        """{"host":"files.example.com","port":2222,"username":"connapse","allowedRoot":"/srv/knowledge"}""";

    private static string SftpSecret(string? passphrase = null) => passphrase is null
        ? """{"privateKey":"-----BEGIN OPENSSH PRIVATE KEY-----\nnot-a-real-key\n"}"""
        : $$"""{"privateKey":"-----BEGIN OPENSSH PRIVATE KEY-----\nnot-a-real-key\n","passphrase":"{{passphrase}}"}""";

    [Fact]
    public void Create_SftpSource_RecombinesTheConnectionAndScope()
    {
        var connection = MakeConnection(ConnectionProvider.Sftp, SftpConfig);
        var source = MakeSource(connection.Id,
            """{"subPath":"docs","includePatterns":["*.md"],"excludePatterns":["*.tmp"]}""");

        var connector = (SftpConnector)_factory.Create(source, connection, SftpSecret());

        connector.Type.Should().Be(ConnectorType.Sftp);
        connector.Config.Host.Should().Be("files.example.com");
        connector.Config.Port.Should().Be(2222);
        connector.Config.Username.Should().Be("connapse");
        connector.Config.AllowedRoot.Should().Be("/srv/knowledge");
        connector.Config.SubPath.Should().Be("docs");
        connector.Config.IncludePatterns.Should().ContainSingle().Which.Should().Be("*.md");
        connector.Config.ExcludePatterns.Should().ContainSingle().Which.Should().Be("*.tmp");
        connector.Config.ConnectionId.Should().Be(connection.Id);
    }

    /// <summary>
    /// The root names a directory on another machine, so the only place it can be resolved is
    /// the server. Resolving it here would be the local-confinement mistake that
    /// SftpPathConfinement exists to avoid, and it would fail open.
    /// </summary>
    [Fact]
    public void Create_SftpSource_DoesNotResolveTheRootLocally()
    {
        var connection = MakeConnection(ConnectionProvider.Sftp, SftpConfig);
        var source = MakeSource(connection.Id, """{"subPath":"docs"}""");

        var connector = (SftpConnector)_factory.Create(source, connection, SftpSecret());

        connector.Config.AllowedRoot.Should().Be("/srv/knowledge",
            "the root must reach the connector verbatim, to be resolved on the server");
        connector.Config.SubPath.Should().Be("docs",
            "the subPath is confined against the server-resolved root, not combined here");
    }

    [Fact]
    public void Create_SftpConnectionWithNoPort_DefaultsTo22()
    {
        var connection = MakeConnection(ConnectionProvider.Sftp,
            """{"host":"h","username":"u","allowedRoot":"/srv"}""");
        var source = MakeSource(connection.Id, "{}");

        var connector = (SftpConnector)_factory.Create(source, connection, SftpSecret());

        connector.Config.Port.Should().Be(22);
    }

    [Fact]
    public void Create_SftpConnectionCarriesThePinnedFingerprint()
    {
        var connection = MakeConnection(ConnectionProvider.Sftp,
            """{"host":"h","username":"u","allowedRoot":"/srv","hostKeyFingerprint":"SHA256:pinned"}""");
        var source = MakeSource(connection.Id, "{}");

        var connector = (SftpConnector)_factory.Create(source, connection, SftpSecret());

        connector.Config.PinnedHostKeyFingerprint.Should().Be("SHA256:pinned");
    }

    [Fact]
    public void Create_SftpConnectionWithNoFingerprint_LeavesItNullForFirstUse()
    {
        var connection = MakeConnection(ConnectionProvider.Sftp, SftpConfig);
        var source = MakeSource(connection.Id, "{}");

        var connector = (SftpConnector)_factory.Create(source, connection, SftpSecret());

        connector.Config.PinnedHostKeyFingerprint.Should().BeNull();
    }

    [Fact]
    public void Create_SftpCredentialWithPassphrase_IsCarriedThrough()
    {
        var connection = MakeConnection(ConnectionProvider.Sftp, SftpConfig);
        var source = MakeSource(connection.Id, "{}");

        var connector = (SftpConnector)_factory.Create(source, connection, SftpSecret("hunter2"));

        connector.Config.Credential!.Passphrase.Should().Be("hunter2");
        connector.Config.Credential.PrivateKey.Should().Contain("OPENSSH PRIVATE KEY");
    }

    [Theory]
    [InlineData("""{"username":"u","allowedRoot":"/srv"}""")]
    [InlineData("""{"host":"h","allowedRoot":"/srv"}""")]
    [InlineData("""{"host":"h","username":"u"}""")]
    public void Create_SftpConnectionMissingARequiredField_Throws(string config)
    {
        var connection = MakeConnection(ConnectionProvider.Sftp, config);
        var source = MakeSource(connection.Id, "{}");

        Action act = () => _factory.Create(source, connection, SftpSecret());

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// The failure has to be loud and name the connection. A connector built with no key
    /// would fail later, from inside the sync loop, with whatever SSH.NET says about an
    /// empty authentication method.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"passphrase":"only"}""")]
    [InlineData("""{"privateKey":""}""")]
    public void Create_SftpSourceWithNoUsableKey_ThrowsNamingTheConnection(string? secret)
    {
        var connection = MakeConnection(ConnectionProvider.Sftp, SftpConfig);
        var source = MakeSource(connection.Id, "{}");

        Action act = () => _factory.Create(source, connection, secret);

        act.Should().Throw<InvalidOperationException>().WithMessage("*conn*");
    }

    /// <summary>
    /// The no-stored-cloud-credentials position, asserted rather than assumed: adding a
    /// secret parameter to the factory must not make one reachable by the cloud providers.
    /// </summary>
    [Theory]
    [InlineData(ConnectionProvider.S3, """{"region":"eu-west-1"}""", """{"bucketName":"b"}""")]
    [InlineData(ConnectionProvider.AzureBlob, """{"storageAccountName":"a"}""", """{"containerName":"c"}""")]
    public void Create_CloudProviders_IgnoreASuppliedSecret(
        ConnectionProvider provider, string config, string scope)
    {
        var connection = MakeConnection(provider, config);
        var source = MakeSource(connection.Id, scope);

        var withSecret = _factory.Create(source, connection, "a pasted access key");
        var without = _factory.Create(source, connection);

        withSecret.Type.Should().Be(without.Type,
            "a secret must not change how a cloud connector authenticates");
    }
}

