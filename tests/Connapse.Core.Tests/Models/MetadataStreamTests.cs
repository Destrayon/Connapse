using Connapse.Core;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Models;

/// <summary>Metadata a connector learns only by reading a file, merged into the job's (CON-34).</summary>
[Trait("Category", "Unit")]
public sealed class MetadataStreamTests
{
    private static IngestionOptions Job() => new(
        DocumentId: "d",
        Metadata: new Dictionary<string, string>
        {
            ["RemoteSize"] = "12",
            ["jira:issueKey"] = "FLINK-1",
            ["jira:links"] = "blocks FLINK-9",
        });

    [Fact]
    public void Merge_PlainStream_LeavesTheJobAsItWas()
    {
        IngestionOptions job = Job();

        MetadataStream.Merge(job, new MemoryStream([1])).Should().BeSameAs(job);
    }

    [Fact]
    public void Merge_ReadMetadata_ReplacesTheSameKeys()
    {
        using MetadataStream read = new([1], new Dictionary<string, string>
        {
            ["jira:links"] = "",
            ["jira:parent"] = "FLINK-2",
        });

        IngestionOptions merged = MetadataStream.Merge(Job(), read);

        merged.Metadata.Should().Equal(new Dictionary<string, string>
        {
            ["RemoteSize"] = "12",
            ["jira:issueKey"] = "FLINK-1",
            ["jira:links"] = "",
            ["jira:parent"] = "FLINK-2",
        });
    }

    [Fact]
    public void Merge_UnnamespacedKey_CannotOverwriteTheSyncsOwn()
    {
        using MetadataStream read = new([1], new Dictionary<string, string> { ["RemoteSize"] = "999" });

        MetadataStream.Merge(Job(), read).Metadata!["RemoteSize"].Should().Be("12");
    }

    [Fact]
    public void Merge_JobWithNoMetadata_TakesTheRead()
    {
        using MetadataStream read = new([1], new Dictionary<string, string> { ["jira:parent"] = "FLINK-2" });

        MetadataStream.Merge(new IngestionOptions(), read).Metadata.Should()
            .Equal(new Dictionary<string, string> { ["jira:parent"] = "FLINK-2" });
    }

    [Fact]
    public void Read_ReturnsTheContent()
    {
        using MetadataStream read = new("abc"u8.ToArray(), new Dictionary<string, string>());

        new StreamReader(read).ReadToEnd().Should().Be("abc");
    }
}
