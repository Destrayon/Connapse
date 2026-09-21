using Connapse.Core;
using Connapse.Storage.CloudScope;
using FluentAssertions;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class BlobTagReaderTests
{
    [Fact]
    public void MapTags_CopiesAllPairs()
    {
        IReadOnlyDictionary<string, string> result = BlobTagReader.MapTags(
            new Dictionary<string, string> { ["Project"] = "Cascade", ["Env"] = "prod" });
        result.Should().HaveCount(2);
        result["Project"].Should().Be("Cascade");
        result["Env"].Should().Be("prod");
    }

    [Fact]
    public void MapTags_Null_ReturnsEmpty() =>
        BlobTagReader.MapTags(null).Should().BeEmpty();
}
