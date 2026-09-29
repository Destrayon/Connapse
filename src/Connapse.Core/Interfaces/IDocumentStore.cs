namespace Connapse.Core.Interfaces;

public interface IDocumentStore
{
    Task<StoreResult> StoreAsync(Document document, CancellationToken ct = default);
    Task<Document?> GetAsync(string documentId, CancellationToken ct = default);
    Task<IReadOnlyList<Document>> ListAsync(Guid containerId, string? pathPrefix = null, int skip = 0, int take = 50, CancellationToken ct = default);
    Task DeleteAsync(string documentId, CancellationToken ct = default);
    Task<bool> ExistsByPathAsync(Guid containerId, string path, CancellationToken ct = default);
    Task<Document?> GetByPathAsync(Guid containerId, string path, CancellationToken ct = default);
    Task<ContainerStats> GetContainerStatsAsync(Guid containerId, CancellationToken ct = default);
    Task UpdateSummaryAsync(string documentId, string? summary, DateTime? generatedAt, string? contentHash, CancellationToken ct = default);

    /// <summary>
    /// Clears the cached per-document summary fields (Summary, SummaryGeneratedAt, SummaryContentHash)
    /// for every document in a container. Returns the number of rows affected. Embeddings, chunks, and
    /// the documents themselves are left intact — this only drops the summary cache so it can be
    /// regenerated. Pairs with <see cref="IContainerStore.UpdateSummaryAsync"/> (nulls) to fully reset
    /// a container's summarization state.
    /// </summary>
    Task<int> ClearDocumentSummariesAsync(Guid containerId, CancellationToken ct = default);

    /// <summary>
    /// The document's text rebuilt from its stored chunks in order, without the overlap between
    /// neighbouring chunks. Null when it has no chunks.
    /// </summary>
    Task<string?> GetDocumentTextAsync(string documentId, CancellationToken ct = default);

    /// <summary>
    /// Returns container IDs whose docs have summaries newer than the container's own summary
    /// (or whose container has no summary at all but some docs do). Used by the hourly sweep
    /// to catch any containers missed by event-driven rollup triggering.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindContainersWithStaleSummariesAsync(CancellationToken ct = default);

    /// <summary>
    /// The <c>resource_uri</c> of each requested document (null when it has none — uploads and non-cloud
    /// connectors — or the id is unknown). One batched lookup, for the search verifier which must map
    /// ranked hits back to their governing URIs.
    /// </summary>
    Task<IReadOnlyDictionary<string, string?>> GetResourceUrisAsync(
        IReadOnlyCollection<string> documentIds, CancellationToken ct = default);
}
