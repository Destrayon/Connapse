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
}
