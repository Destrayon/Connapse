using Connapse.Core.Interfaces;

namespace Connapse.Eval.Tests.Systems;

/// <summary>Deterministic bag-of-words embedder so integration tests need no Ollama.</summary>
public sealed class HashingEmbeddingProvider : IEmbeddingProvider
{
    public int Dimensions => 64;

    public string ModelId => "hashing-test";

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult(Embed(text));

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Embed).ToList());

    private float[] Embed(string text)
    {
        float[] vector = new float[Dimensions];
        foreach (string token in text.ToLowerInvariant().Split(' ', '.', ',', '\n', '\t'))
            if (token.Length > 2)
                vector[(int)((uint)StringComparer.Ordinal.GetHashCode(token) % Dimensions)] += 1;
        double norm = Math.Sqrt(vector.Sum(v => v * v));
        return norm == 0 ? vector : vector.Select(v => (float)(v / norm)).ToArray();
    }
}
