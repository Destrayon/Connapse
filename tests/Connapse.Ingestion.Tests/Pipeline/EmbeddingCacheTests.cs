using Connapse.Ingestion.Pipeline;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Pipeline;

[Trait("Category", "Unit")]
public class EmbeddingCacheTests
{
    [Fact]
    public void ComputeHash_SameContent_ReturnsSameHash()
    {
        var h1 = EmbeddingCache.ComputeHash("hello world");
        var h2 = EmbeddingCache.ComputeHash("hello world");
        h1.Should().Be(h2);
    }

    [Fact]
    public void ComputeHash_DifferentContent_ReturnsDifferentHash()
    {
        var h1 = EmbeddingCache.ComputeHash("hello world");
        var h2 = EmbeddingCache.ComputeHash("goodbye world");
        h1.Should().NotBe(h2);
    }

    [Fact]
    public void ComputeHash_KnownInput_ReturnsExpectedDigest()
    {
        string hash = EmbeddingCache.ComputeHash("hello world");
        hash.Should().Be("b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9");
    }

    [Fact]
    public void Match_RowLabelledWithTheSettingButNativeLength_IsAMiss()
    {
        // Before #666 a model could return 4096 numbers while the row recorded Dimensions = 1536.
        var rows = new[] { ("a", new float[4096]), ("b", new float[1536]) };

        var cached = EmbeddingCache.Match(["a", "b", "c"], rows, dimensions: 1536);

        cached[0].Should().BeNull();
        cached[1].Should().HaveCount(1536);
        cached[2].Should().BeNull();
    }

    [Fact]
    public void Match_SameHashTwiceOneWrongLength_UsesTheRightOne()
    {
        var rows = new[] { ("a", new float[4096]), ("a", new float[1536]) };

        var cached = EmbeddingCache.Match(["a"], rows, dimensions: 1536);

        cached[0].Should().HaveCount(1536);
    }
}
