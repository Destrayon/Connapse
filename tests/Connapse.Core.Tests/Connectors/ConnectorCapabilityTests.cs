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
    [InlineData(typeof(AzureBlobConnector))]
    [InlineData(typeof(FilesystemConnector))]
    public void ExternalConnectors_AreNotWritable(Type connectorType)
    {
        typeof(IWritableConnector).IsAssignableFrom(connectorType)
            .Should().BeFalse("external storage is mirrored, never mutated through Connapse");
    }
}
