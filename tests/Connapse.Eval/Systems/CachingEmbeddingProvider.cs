using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Eval.Systems;

/// <summary>
/// Caches embeddings on disk keyed by (provider type, model, dimensions, text), so reruns re-embed only changed chunks.
/// The text in the key is the text as the model sees it, prepared for <paramref name="settings"/>, so a prefixed and an
/// unprefixed embedding of the same chunk never share an entry.
/// </summary>
public sealed class CachingEmbeddingProvider(IEmbeddingProvider inner, EmbeddingDiskCache cache, EmbeddingSettings settings) : IEmbeddingProvider
{
    private string Namespace => $"{inner.GetType().Name}-{inner.ModelId}-{inner.Dimensions}";

    public int Dimensions => inner.Dimensions;

    public string ModelId => inner.ModelId;

    public async Task<float[]> EmbedAsync(string text, EmbeddingInputType inputType, CancellationToken ct = default)
    {
        string key = EmbeddingText.Prepare(settings, [text], inputType)[0];
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
        IReadOnlyList<string> keys = EmbeddingText.Prepare(settings, list, inputType);
        for (int i = 0; i < list.Count; i++)
        {
            float[]? cached = cache.TryGet(Namespace, keys[i]);
            if (cached is null)
                missing.Add(i);
            else
                result[i] = cached;
        }

        // One provider batch at a time, each saved as it returns: a run stopped part way through a huge call (the
        // semantic chunker embeds every sentence of a document in one) keeps what it already embedded.
        foreach (int[] slice in missing.Chunk(Math.Max(1, settings.BatchSize)))
        {
            IReadOnlyList<float[]> fresh = await inner.EmbedBatchAsync(slice.Select(i => list[i]), inputType, ct);
            for (int j = 0; j < slice.Length; j++)
            {
                result[slice[j]] = fresh[j];
                cache.Put(Namespace, keys[slice[j]], fresh[j]);
            }
        }
        return result;
    }
}
