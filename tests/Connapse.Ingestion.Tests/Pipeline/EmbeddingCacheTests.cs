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
}
