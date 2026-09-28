using Connapse.Core;
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
        CachingEmbeddingProvider provider = new(inner, new EmbeddingDiskCache(_root), EmbeddingPrompts.None);

        float[] first = await provider.EmbedAsync("hello", EmbeddingInputType.Document);
        float[] second = await provider.EmbedAsync("hello", EmbeddingInputType.Document);

        inner.Calls.Should().Be(1);
        second.Should().Equal(first);
    }

    [Fact]
    public async Task EmbedBatchAsync_MixedCachedAndNew_EmbedsOnlyNewAndKeepsOrder()
    {
        CountingProvider inner = new();
        CachingEmbeddingProvider provider = new(inner, new EmbeddingDiskCache(_root), EmbeddingPrompts.None);
        await provider.EmbedAsync("bb", EmbeddingInputType.Document);

        IReadOnlyList<float[]> vectors = await provider.EmbedBatchAsync(["a", "bb", "ccc"], EmbeddingInputType.Document);

        inner.Calls.Should().Be(3, "one earlier call plus two uncached texts");
        vectors.Select(v => v[0]).Should().Equal(1f, 2f, 3f);
    }

    [Fact]
    public async Task EmbedAsync_NewCacheInstanceSameDirectory_ReadsFromDisk()
    {
        await new CachingEmbeddingProvider(new CountingProvider(), new EmbeddingDiskCache(_root), EmbeddingPrompts.None).EmbedAsync("persist", EmbeddingInputType.Document);
        CountingProvider inner = new();

        await new CachingEmbeddingProvider(inner, new EmbeddingDiskCache(_root), EmbeddingPrompts.None).EmbedAsync("persist", EmbeddingInputType.Document);

        inner.Calls.Should().Be(0);
    }

    [Fact]
    public async Task EmbedAsync_SameModelDifferentDimensions_DoesNotShareEntries()
    {
        await new CachingEmbeddingProvider(new CountingProvider(dimensions: 2), new EmbeddingDiskCache(_root), EmbeddingPrompts.None).EmbedAsync("dims", EmbeddingInputType.Document);
        CountingProvider inner = new(dimensions: 3);

        await new CachingEmbeddingProvider(inner, new EmbeddingDiskCache(_root), EmbeddingPrompts.None).EmbedAsync("dims", EmbeddingInputType.Document);

        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task EmbedAsync_SameTextPromptedAndUnprompted_DoesNotShareEntries()
    {
        CountingProvider inner = new();
        CachingEmbeddingProvider provider = new(inner, new EmbeddingDiskCache(_root), new EmbeddingPrompts("q: ", "d: "));

        await provider.EmbedAsync("text", EmbeddingInputType.Document);
        await provider.EmbedAsync("text", EmbeddingInputType.Query);
        await provider.EmbedAsync("text", EmbeddingInputType.Unspecified);
        await provider.EmbedAsync("text", EmbeddingInputType.Document);

        inner.Calls.Should().Be(3);
    }

    private sealed class CountingProvider(int dimensions = 2) : IEmbeddingProvider
    {
        public int Calls { get; private set; }
        public int Dimensions => dimensions;
        public string ModelId => "counting";

        public Task<float[]> EmbedAsync(string text, EmbeddingInputType inputType, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new float[] { text.Length, 1 });
        }

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, EmbeddingInputType inputType, CancellationToken ct = default)
        {
            List<string> list = texts.ToList();
            Calls += list.Count;
            return Task.FromResult<IReadOnlyList<float[]>>(list.Select(t => new float[] { t.Length, 1 }).ToList());
        }
    }
}
