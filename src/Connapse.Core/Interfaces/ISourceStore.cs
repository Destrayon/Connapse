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
    /// Unconditionally persists the sync cursor and outcome. Passing a null cursor clears
    /// it, which is what a RequiresFullResync response demands.
    /// <para>
    /// This overwrites whatever is stored, so it is only safe when the caller is
    /// deliberately resetting progress (a full resync, or recording a failure). For normal
    /// forward advancement use <see cref="TryAdvanceSyncStateAsync"/>, which cannot clobber
    /// a concurrent sync's newer cursor.
    /// </para>
    /// </summary>
    Task UpdateSyncStateAsync(Guid id, string? cursor, SyncStatus status, string? error, DateTime? syncedAt, CancellationToken ct = default);

    /// <summary>
    /// Advances the sync cursor only if the stored cursor still equals <paramref name="expectedCursor"/>,
    /// as a single atomic statement. Returns false when another sync already moved it, in which case
    /// nothing is written and the caller should discard its result rather than retry blindly.
    /// <para>
    /// Without this, two overlapping syncs racing to completion can regress progress: if B starts
    /// after A but finishes first, A's unconditional write replaces B's newer cursor and the next
    /// cycle resumes from stale progress — duplicate ingestion at best, and an invalid continuation
    /// token for providers whose cursors are opaque.
    /// </para>
    /// </summary>
    Task<bool> TryAdvanceSyncStateAsync(Guid id, string? expectedCursor, string? newCursor, SyncStatus status, string? error, DateTime? syncedAt, CancellationToken ct = default);

    Task<ContainerSettingsOverrides?> GetSettingsOverridesAsync(Guid id, CancellationToken ct = default);
    Task SaveSettingsOverridesAsync(Guid id, ContainerSettingsOverrides overrides, CancellationToken ct = default);
    Task UpdateSummaryAsync(Guid id, string? summary, DateTime? generatedAt, string? docSetHash, CancellationToken ct = default);
}
