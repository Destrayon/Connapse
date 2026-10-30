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

    /// <summary>
    /// Saves <paramref name="overrides"/> storing only the search settings named in
    /// <paramref name="searchSettingsNamed"/>: one left out stays the deployment's rather than becoming the default
    /// the typed object carries.
    /// </summary>
    Task SaveSettingsOverridesAsync(
        Guid id, ContainerSettingsOverrides overrides, IEnumerable<string> searchSettingsNamed,
        CancellationToken ct = default);

    /// <summary>
    /// The overrides as stored, so a search setting that was never chosen stays absent rather than reading back as
    /// its default. Null when the container has none.
    /// </summary>
    Task<System.Text.Json.JsonElement?> GetSettingsOverridesJsonAsync(Guid id, CancellationToken ct = default);
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
