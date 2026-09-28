using System.Collections.Concurrent;
using Connapse.Storage.Vectors;

namespace Connapse.Search.Vector;

/// <summary>
/// A query embedded for one vector space: it is only comparable with vectors stored under
/// <paramref name="ModelId"/>, so searching and scoring both filter on it.
/// </summary>
public sealed record QueryEmbedding(float[] Vector, string ModelId);

/// <summary>
/// How many vectors each model id holds, per container, remembered briefly. Only consulted while
/// the configured model has prompts, to notice vectors stored before prompts applied; a search
/// should not pay a GROUP BY over the container on every query.
/// </summary>
public sealed class VectorModelCountCache(TimeProvider time)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, IReadOnlyList<EmbeddingModelInfo> Models)> _byContainer = new();

    public async Task<IReadOnlyList<EmbeddingModelInfo>> GetAsync(
        Guid? containerId, VectorModelDiscovery discovery, CancellationToken ct)
    {
        Guid key = containerId ?? Guid.Empty;
        DateTimeOffset now = time.GetUtcNow();
        if (_byContainer.TryGetValue(key, out var cached) && now - cached.At < Lifetime)
            return cached.Models;

        IReadOnlyList<EmbeddingModelInfo> models = await discovery.GetModelsAsync(containerId, ct);
        _byContainer[key] = (now, models);
        return models;
    }
}
