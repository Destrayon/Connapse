using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

[Trait("Category", "Unit")]
public class ConnectorCapabilityTests
{
    [Fact]
    public void IConnector_HasNoWriteSurface()
    {
        // A source holds an IConnector. If write methods live here, "read-only" is a
        // runtime promise; moving them to IWritableConnector makes it a type guarantee.
        var names = typeof(IConnector).GetMethods().Select(m => m.Name).ToList();

        names.Should().NotContain("WriteFileAsync");
        names.Should().NotContain("DeleteFileAsync");
        typeof(IConnector).GetProperty("SupportsWrite").Should().BeNull();
    }

    [Fact]
    public void MinioConnector_IsWritable()
    {
        typeof(IWritableConnector).IsAssignableFrom(typeof(MinioConnector))
            .Should().BeTrue("managed storage is the only writable backend");
    }

    [Theory]
    [InlineData(typeof(S3Connector))]
    [InlineData(typeof(FilesystemConnector))]
    [InlineData(typeof(SftpConnector))]
    public void ExternalConnectors_AreNotWritable(Type connectorType)
    {
        typeof(IWritableConnector).IsAssignableFrom(connectorType)
            .Should().BeFalse("external storage is mirrored, never mutated through Connapse");
    }

    /// <summary>
    /// SFTP has no change notification of any kind, so claiming live watch would put the sync
    /// engine on a path that cannot work. It must say so, and WatchAsync must refuse rather
    /// than return an empty sequence that looks like a quiet remote.
    /// </summary>
    [Fact]
    public void SftpConnector_DoesNotClaimLiveWatch()
    {
        var connector = new SftpConnector(new SftpConnectorConfig { Host = "h", AllowedRoot = "/srv" });

        connector.SupportsLiveWatch.Should().BeFalse();

        Action act = () => connector.WatchAsync();
        act.Should().Throw<NotSupportedException>();
    }

    /// <summary>
    /// A connector that holds a live SSH session must be disposable, or the sync loop leaks
    /// one per source per cycle — the reason SourceSyncService disposes in a finally.
    /// </summary>
    [Fact]
    public void SftpConnector_IsDisposable()
    {
        typeof(IDisposable).IsAssignableFrom(typeof(SftpConnector))
            .Should().BeTrue("it owns an SSH session that a five-minute poll would otherwise abandon");
    }
}
