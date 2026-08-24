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
    /// Updates only the IngestionState column. Used by Hangfire job classes
    /// as they transition through Pending → Indexed → SummaryIndexed → (Failed).
    /// </summary>
    Task UpdateIngestionStateAsync(string documentId, IngestionState state, CancellationToken ct = default);

    /// <summary>
    /// Records that a document's ingestion failed, in both the enrichment state and the job
    /// lifecycle status.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="UpdateIngestionStateAsync"/> because that one also carries
    /// summarization outcomes, and a summary that failed does not make an indexed document a
    /// failed one. This is only for the ingestion job itself.
    /// <para>
    /// Both columns matter: the sync engine reads <c>Status</c>, and treats "Pending" as a job
    /// that is still coming. A job that died before the pipeline loaded the row leaves that
    /// status behind, and the document is then skipped on every future cycle.
    /// </para>
    /// </remarks>
    Task MarkIngestionFailedAsync(string documentId, string? errorMessage, CancellationToken ct = default);

    /// <summary>
    /// Returns container IDs whose docs have summaries newer than the container's own summary
    /// (or whose container has no summary at all but some docs do). Used by the hourly sweep
    /// to catch any containers missed by event-driven rollup triggering.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindContainersWithStaleSummariesAsync(CancellationToken ct = default);
}
