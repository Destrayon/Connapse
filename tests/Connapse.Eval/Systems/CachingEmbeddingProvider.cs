using Connapse.Core.Interfaces;

namespace Connapse.Eval.Systems;

/// <summary>Caches embeddings on disk keyed by (provider type, model, text), so reruns re-embed only changed chunks.</summary>
public sealed class CachingEmbeddingProvider(IEmbeddingProvider inner, EmbeddingDiskCache cache) : IEmbeddingProvider
{
    private string Namespace => $"{inner.GetType().Name}-{inner.ModelId}";

    public int Dimensions => inner.Dimensions;

    public string ModelId => inner.ModelId;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        float[]? cached = cache.TryGet(Namespace, text);
        if (cached is not null)
            return cached;
        float[] vector = await inner.EmbedAsync(text, ct);
        cache.Put(Namespace, text, vector);
        return vector;
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        List<string> list = texts.ToList();
        float[][] result = new float[list.Count][];
        List<int> missing = [];
        for (int i = 0; i < list.Count; i++)
        {
            float[]? cached = cache.TryGet(Namespace, list[i]);
            if (cached is null)
                missing.Add(i);
            else
                result[i] = cached;
        }

        if (missing.Count > 0)
        {
            IReadOnlyList<float[]> fresh = await inner.EmbedBatchAsync(missing.Select(i => list[i]), ct);
            for (int j = 0; j < missing.Count; j++)
            {
                result[missing[j]] = fresh[j];
                cache.Put(Namespace, list[missing[j]], fresh[j]);
            }
        }
        return result;
    }
}
