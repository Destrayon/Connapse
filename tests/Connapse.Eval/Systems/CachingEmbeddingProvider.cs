using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Eval.Systems;

/// <summary>
/// Caches embeddings on disk keyed by (provider type, model, dimensions, text), so reruns re-embed only changed chunks.
/// The text in the key is the text as the model sees it, with <paramref name="prompts"/> applied, so a prefixed and an
/// unprefixed embedding of the same chunk never share an entry.
/// </summary>
public sealed class CachingEmbeddingProvider(IEmbeddingProvider inner, EmbeddingDiskCache cache, EmbeddingPrompts prompts) : IEmbeddingProvider
{
    private string Namespace => $"{inner.GetType().Name}-{inner.ModelId}-{inner.Dimensions}";

    public int Dimensions => inner.Dimensions;

    public string ModelId => inner.ModelId;

    public async Task<float[]> EmbedAsync(string text, EmbeddingInputType inputType, CancellationToken ct = default)
    {
        string key = prompts.Apply(text, inputType);
        float[]? cached = cache.TryGet(Namespace, key);
        if (cached is not null)
            return cached;
        float[] vector = await inner.EmbedAsync(text, inputType, ct);
        cache.Put(Namespace, key, vector);
        return vector;
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, EmbeddingInputType inputType, CancellationToken ct = default)
    {
        List<string> list = texts.ToList();
        float[][] result = new float[list.Count][];
        List<int> missing = [];
        for (int i = 0; i < list.Count; i++)
        {
            float[]? cached = cache.TryGet(Namespace, prompts.Apply(list[i], inputType));
            if (cached is null)
                missing.Add(i);
            else
                result[i] = cached;
        }

        if (missing.Count > 0)
        {
            IReadOnlyList<float[]> fresh = await inner.EmbedBatchAsync(missing.Select(i => list[i]), inputType, ct);
            for (int j = 0; j < missing.Count; j++)
            {
                result[missing[j]] = fresh[j];
                cache.Put(Namespace, prompts.Apply(list[missing[j]], inputType), fresh[j]);
            }
        }
        return result;
    }
}
