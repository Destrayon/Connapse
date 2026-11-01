namespace Connapse.Core.Interfaces;

public interface IVectorStore
{
    Task UpsertAsync(string id, float[] vector, Dictionary<string, string> metadata, CancellationToken ct = default);
    Task UpsertBatchAsync(IReadOnlyList<(string Id, float[] Vector, Dictionary<string, string> Metadata)> items, CancellationToken ct = default);
    /// <param name="scopes">
    /// What the caller may reach. Required rather than optional: a default would make forgetting
    /// it compile, and forgetting it here returns everything to everyone.
    /// </param>
    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(float[] queryVector, int topK, Dictionary<string, string>? filters, SearchScopes scopes, CancellationToken ct = default);
    /// <summary>
    /// Cosine similarity between <paramref name="queryVector"/> and the named chunks' vectors for
    /// <paramref name="modelId"/>. Scores only — it applies no permission filter, so callers pass
    /// chunks that have already been admitted by a scoped search. A chunk with no vector for the
    /// model is absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<string, float>> ScoreChunksAsync(float[] queryVector, IReadOnlyCollection<string> chunkIds, string modelId, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task DeleteByDocumentIdAsync(string documentId, CancellationToken ct = default);
}
