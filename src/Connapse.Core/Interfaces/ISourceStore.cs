namespace Connapse.Core.Interfaces;

public interface ISourceStore
{
    Task<Source> CreateAsync(CreateSourceRequest request, CancellationToken ct = default);
    Task<Source?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Source?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<Source>> ListAsync(int skip = 0, int take = 50, CancellationToken ct = default);
    Task<IReadOnlyList<Source>> ListByConnectionAsync(Guid connectionId, CancellationToken ct = default);
    Task<Source?> UpdateAsync(Guid id, UpdateSourceRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<bool> ExistsAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Persists the sync cursor and outcome after a sync cycle. Passing a null
    /// cursor clears it, which is what a RequiresFullResync response demands.
    /// </summary>
    Task UpdateSyncStateAsync(Guid id, string? cursor, SyncStatus status, string? error, DateTime? syncedAt, CancellationToken ct = default);

    Task<ContainerSettingsOverrides?> GetSettingsOverridesAsync(Guid id, CancellationToken ct = default);
    Task SaveSettingsOverridesAsync(Guid id, ContainerSettingsOverrides overrides, CancellationToken ct = default);
    Task UpdateSummaryAsync(Guid id, string? summary, DateTime? generatedAt, string? docSetHash, CancellationToken ct = default);
}
