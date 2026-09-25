using Connapse.Core.Interfaces;
using Connapse.Eval.Systems;
using FluentAssertions;

namespace Connapse.Eval.Tests.Systems;

[Trait("Category", "Unit")]
public class CachingEmbeddingProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eval-emb-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task EmbedAsync_SameTextTwice_CallsInnerOnce()
    {
        CountingProvider inner = new();
        CachingEmbeddingProvider provider = new(inner, new EmbeddingDiskCache(_root));

        float[] first = await provider.EmbedAsync("hello");
        float[] second = await provider.EmbedAsync("hello");

        inner.Calls.Should().Be(1);
        second.Should().Equal(first);
    }

    [Fact]
    public async Task EmbedBatchAsync_MixedCachedAndNew_EmbedsOnlyNewAndKeepsOrder()
    {
        CountingProvider inner = new();
        CachingEmbeddingProvider provider = new(inner, new EmbeddingDiskCache(_root));
        await provider.EmbedAsync("bb");

        IReadOnlyList<float[]> vectors = await provider.EmbedBatchAsync(["a", "bb", "ccc"]);

        inner.Calls.Should().Be(3, "one earlier call plus two uncached texts");
        vectors.Select(v => v[0]).Should().Equal(1f, 2f, 3f);
    }

    [Fact]
    public async Task EmbedAsync_NewCacheInstanceSameDirectory_ReadsFromDisk()
    {
        await new CachingEmbeddingProvider(new CountingProvider(), new EmbeddingDiskCache(_root)).EmbedAsync("persist");
        CountingProvider inner = new();

        await new CachingEmbeddingProvider(inner, new EmbeddingDiskCache(_root)).EmbedAsync("persist");

        inner.Calls.Should().Be(0);
    }

    private sealed class CountingProvider : IEmbeddingProvider
    {
        public int Calls { get; private set; }
        public int Dimensions => 2;
        public string ModelId => "counting";

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new float[] { text.Length, 1 });
        }

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
        {
            List<string> list = texts.ToList();
            Calls += list.Count;
            return Task.FromResult<IReadOnlyList<float[]>>(list.Select(t => new float[] { t.Length, 1 }).ToList());
        }
    }
}
