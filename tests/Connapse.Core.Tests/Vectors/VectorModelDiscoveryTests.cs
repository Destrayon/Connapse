using Connapse.Storage.Vectors;
using FluentAssertions;

namespace Connapse.Core.Tests.Vectors;

[Trait("Category", "Unit")]
public class VectorModelDiscoveryTests
{
    [Fact]
    public void EmbeddingModelInfo_RecordProperties_ReturnCorrectValues()
    {
        var info = new EmbeddingModelInfo("nomic-embed-text", 768, 1500);

        info.ModelId.Should().Be("nomic-embed-text");
        info.Dimensions.Should().Be(768);
        info.VectorCount.Should().Be(1500);
    }

    [Fact]
    public void EmbeddingModelInfo_Equality_WorksAsExpected()
    {
        var a = new EmbeddingModelInfo("model-a", 768, 100);
        var b = new EmbeddingModelInfo("model-a", 768, 100);
        var c = new EmbeddingModelInfo("model-b", 1536, 200);

        a.Should().Be(b);
        a.Should().NotBe(c);
    }

    [Fact]
    public void EmbeddingModelInfo_Deconstruction_Works()
    {
        var info = new EmbeddingModelInfo("text-embedding-3-small", 1536, 500);
        var (modelId, dims, count) = info;

        modelId.Should().Be("text-embedding-3-small");
        dims.Should().Be(1536);
        count.Should().Be(500);
    }
}
