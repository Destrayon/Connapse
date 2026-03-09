using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests;

[Trait("Category", "Unit")]
public class ContainerWriteGuardTests
{
    private static Container MakeContainer(
        ConnectorType type, string? connectorConfig = null) =>
        new(
            Id: Guid.NewGuid().ToString(),
            Name: "test",
            Description: null,
            ConnectorType: type,
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            ConnectorConfig: connectorConfig);

    // --- S3 / AzureBlob read-only ---

    [Theory]
    [InlineData(ConnectorType.S3)]
    [InlineData(ConnectorType.AzureBlob)]
    public void CloudConnectors_BlockAllWrites(ConnectorType type)
    {
        var container = MakeContainer(type);

        ContainerWriteGuard.CheckWrite(container, WriteOperation.Upload).Should().NotBeNull();
        ContainerWriteGuard.CheckWrite(container, WriteOperation.Delete).Should().NotBeNull();
        ContainerWriteGuard.CheckWrite(container, WriteOperation.CreateFolder).Should().NotBeNull();
    }

    [Theory]
    [InlineData(ConnectorType.S3)]
    [InlineData(ConnectorType.AzureBlob)]
    public void CloudConnectors_ErrorMentionsConnectorType(ConnectorType type)
    {
        var container = MakeContainer(type);
        var error = ContainerWriteGuard.CheckWrite(container, WriteOperation.Upload);
        error.Should().Contain(type.ToString());
        error.Should().Contain("read-only");
    }

    // --- MinIO (always writable) ---

    [Theory]
    [InlineData(WriteOperation.Upload)]
    [InlineData(WriteOperation.Delete)]
    [InlineData(WriteOperation.CreateFolder)]
    public void MinIO_AllowsAllWrites(WriteOperation op)
    {
        var container = MakeContainer(ConnectorType.MinIO);
        ContainerWriteGuard.CheckWrite(container, op).Should().BeNull();
    }

    // --- Filesystem with default config (all allowed) ---

    [Theory]
    [InlineData(WriteOperation.Upload)]
    [InlineData(WriteOperation.Delete)]
    [InlineData(WriteOperation.CreateFolder)]
    public void Filesystem_DefaultConfig_AllowsAllWrites(WriteOperation op)
    {
        var config = """{"rootPath":"C:/data"}""";
        var container = MakeContainer(ConnectorType.Filesystem, config);
        ContainerWriteGuard.CheckWrite(container, op).Should().BeNull();
    }

    // --- Filesystem with individual flags disabled ---

    [Fact]
    public void Filesystem_AllowUploadFalse_BlocksUpload()
    {
        var config = """{"rootPath":"C:/data","allowUpload":false}""";
        var container = MakeContainer(ConnectorType.Filesystem, config);

        ContainerWriteGuard.CheckWrite(container, WriteOperation.Upload).Should().NotBeNull();
        ContainerWriteGuard.CheckWrite(container, WriteOperation.Delete).Should().BeNull();
        ContainerWriteGuard.CheckWrite(container, WriteOperation.CreateFolder).Should().BeNull();
    }

    [Fact]
    public void Filesystem_AllowDeleteFalse_BlocksDelete()
    {
        var config = """{"rootPath":"C:/data","allowDelete":false}""";
        var container = MakeContainer(ConnectorType.Filesystem, config);

        ContainerWriteGuard.CheckWrite(container, WriteOperation.Delete).Should().NotBeNull();
        ContainerWriteGuard.CheckWrite(container, WriteOperation.Upload).Should().BeNull();
        ContainerWriteGuard.CheckWrite(container, WriteOperation.CreateFolder).Should().BeNull();
    }

    [Fact]
    public void Filesystem_AllowCreateFolderFalse_BlocksCreateFolder()
    {
        var config = """{"rootPath":"C:/data","allowCreateFolder":false}""";
        var container = MakeContainer(ConnectorType.Filesystem, config);

        ContainerWriteGuard.CheckWrite(container, WriteOperation.CreateFolder).Should().NotBeNull();
        ContainerWriteGuard.CheckWrite(container, WriteOperation.Upload).Should().BeNull();
        ContainerWriteGuard.CheckWrite(container, WriteOperation.Delete).Should().BeNull();
    }

    [Fact]
    public void Filesystem_AllFlagsFalse_BlocksEverything()
    {
        var config = """{"rootPath":"C:/data","allowUpload":false,"allowDelete":false,"allowCreateFolder":false}""";
        var container = MakeContainer(ConnectorType.Filesystem, config);

        ContainerWriteGuard.CheckWrite(container, WriteOperation.Upload).Should().NotBeNull();
        ContainerWriteGuard.CheckWrite(container, WriteOperation.Delete).Should().NotBeNull();
        ContainerWriteGuard.CheckWrite(container, WriteOperation.CreateFolder).Should().NotBeNull();
    }

    // --- Filesystem without connector config (null/empty) ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Filesystem_NullOrEmptyConfig_AllowsAllWrites(string? config)
    {
        var container = MakeContainer(ConnectorType.Filesystem, config);

        ContainerWriteGuard.CheckWrite(container, WriteOperation.Upload).Should().BeNull();
        ContainerWriteGuard.CheckWrite(container, WriteOperation.Delete).Should().BeNull();
        ContainerWriteGuard.CheckWrite(container, WriteOperation.CreateFolder).Should().BeNull();
    }

    // --- IsReadOnly convenience method ---

    [Theory]
    [InlineData(ConnectorType.S3, true)]
    [InlineData(ConnectorType.AzureBlob, true)]
    [InlineData(ConnectorType.MinIO, false)]
    [InlineData(ConnectorType.Filesystem, false)]
    public void IsReadOnly_ReturnsExpected(ConnectorType type, bool expected)
    {
        ContainerWriteGuard.IsReadOnly(type).Should().Be(expected);
    }
}
