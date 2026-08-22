namespace Connapse.Core.Interfaces;

public interface IContainerStore
{
    Task<Container> CreateAsync(CreateContainerRequest request, CancellationToken ct = default);
    Task<Container?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Container?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<Container>> ListAsync(int skip = 0, int take = 50, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<bool> ExistsAsync(Guid id, CancellationToken ct = default);
    Task<ContainerSettingsOverrides?> GetSettingsOverridesAsync(Guid id, CancellationToken ct = default);
    Task SaveSettingsOverridesAsync(Guid id, ContainerSettingsOverrides overrides, CancellationToken ct = default);
    Task UpdateSummaryAsync(Guid id, string? summary, DateTime? generatedAt, string? docSetHash, CancellationToken ct = default);

    /// <summary>
    /// Marks the container summary as stale so the next sweep tick (SummaryJobs.SweepStaleContainersAsync)
    /// flags it as needing a rollup. Required because the sweep's staleness query only fires on
    /// "any doc summary is newer than the container summary" — pure deletions (no newer doc) wouldn't
    /// trigger it otherwise, leaving the container summary referencing deleted docs indefinitely.
    ///
    /// Behavior: if any docs with summaries still exist, clears generated-at + hash but keeps the
    /// summary text visible until the next rollup replaces it. If no summarized docs remain, clears
    /// the entire summary so we don't show stale text for an empty container.
    /// </summary>
    Task MarkSummaryStaleAsync(Guid id, CancellationToken ct = default);
}
