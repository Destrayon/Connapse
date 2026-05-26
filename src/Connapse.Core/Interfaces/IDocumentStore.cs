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
    /// Updates only the IngestionState column. Used by Hangfire job classes
    /// as they transition through Pending → Indexed → SummaryIndexed → (Failed).
    /// </summary>
    Task UpdateIngestionStateAsync(string documentId, IngestionState state, CancellationToken ct = default);

    /// <summary>
    /// Returns container IDs whose docs have summaries newer than the container's own summary
    /// (or whose container has no summary at all but some docs do). Used by the hourly sweep
    /// to catch any containers missed by event-driven rollup triggering.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindContainersWithStaleSummariesAsync(CancellationToken ct = default);
}
