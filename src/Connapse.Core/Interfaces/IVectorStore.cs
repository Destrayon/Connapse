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
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task DeleteByDocumentIdAsync(string documentId, CancellationToken ct = default);

    /// <summary>
    /// Returns one mean-pooled embedding per document in the container, computed across
    /// that document's chunk vectors. Used by the <c>document-clustering</c> container
    /// summary path to cluster docs without re-embedding them.
    /// </summary>
    /// <remarks>
    /// Implementations should:
    /// 1. Pick the dominant <c>model_id</c> in the container and filter to vectors of that model.
    ///    Containers can hold mixed-model chunk vectors; clustering requires a single dimensionality.
    /// 2. Skip documents that have zero chunks of the dominant model.
    /// 3. L2-normalize each pooled vector before returning (pgvector AVG does not renormalize).
    /// 4. Log a warning naming the count of documents excluded due to non-dominant model_id.
    /// </remarks>
    Task<IReadOnlyList<(Guid DocumentId, float[] Embedding)>> GetPooledDocumentEmbeddingsAsync(
        Guid containerId,
        CancellationToken ct = default);
}
