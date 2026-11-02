namespace Connapse.Core.Interfaces;

public interface IContainerStore
{
    Task<Container> CreateAsync(CreateContainerRequest request, CancellationToken ct = default);
    Task<Container?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Container?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<Container>> ListAsync(int skip = 0, int take = 50, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Renames a container or changes its description; a null argument is left as it is. Null when
    /// the container doesn't exist. Throws <see cref="InvalidOperationException"/> when the new name
    /// belongs to another container or isn't a valid container name.
    /// </summary>
    Task<Container?> UpdateAsync(Guid id, string? name, string? description, CancellationToken ct = default);
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

    /// <summary>
    /// Replaces a container's overrides with what <paramref name="update"/> makes of the stored ones, with no other save
    /// landing in between. Answers what was saved, or null when the container doesn't exist.
    /// </summary>
    Task<ContainerSettingsOverrides?> UpdateSettingsOverridesAsync(
        Guid id, Func<ContainerSettingsOverrides?, ContainerSettingsOverrides> update, CancellationToken ct = default);

    /// <summary>
    /// As <see cref="UpdateSettingsOverridesAsync(Guid, Func{ContainerSettingsOverrides?, ContainerSettingsOverrides}, CancellationToken)"/>,
    /// storing only the search settings named in <paramref name="searchSettingsNamed"/>.
    /// </summary>
    Task<ContainerSettingsOverrides?> UpdateSettingsOverridesAsync(
        Guid id, Func<ContainerSettingsOverrides?, ContainerSettingsOverrides> update, IEnumerable<string> searchSettingsNamed,
        CancellationToken ct = default);
}
