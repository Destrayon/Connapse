namespace Connapse.Core.Interfaces;

public interface IContainerStore
{
    Task<Container> CreateAsync(CreateContainerRequest request, CancellationToken ct = default);
    /// <summary>A team container by id. A personal memory space is never found this way: say who is asking instead.</summary>
    Task<Container?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>A container by id, or null when it doesn't exist or is someone else's personal space.</summary>
    Task<Container?> GetAsync(Guid id, MemoryCaller? viewer, CancellationToken ct = default);

    /// <summary>A team container by name. A personal memory space is never found this way.</summary>
    Task<Container?> GetByNameAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Every container with this name that <paramref name="viewer"/> may see: the team container first, then the
    /// viewer's own personal space of the same name. Empty when there is neither.
    /// </summary>
    Task<IReadOnlyList<Container>> FindByNameAsync(string name, MemoryCaller? viewer, CancellationToken ct = default);

    /// <summary>Team containers by name. Personal memory spaces are never listed this way.</summary>
    Task<IReadOnlyList<Container>> ListAsync(int skip = 0, int take = 50, CancellationToken ct = default);

    /// <summary>Team containers and <paramref name="viewer"/>'s own personal spaces, by name.</summary>
    Task<IReadOnlyList<Container>> ListAsync(int skip, int take, MemoryCaller? viewer, CancellationToken ct = default);

    /// <summary>
    /// As <see cref="ListAsync(int, int, MemoryCaller?, CancellationToken)"/>, only containers of <paramref name="kind"/>:
    /// the container list asks for document containers, since memory spaces are listed on their own.
    /// </summary>
    Task<IReadOnlyList<Container>> ListAsync(int skip, int take, MemoryCaller? viewer, ContainerKind kind, CancellationToken ct = default);

    /// <summary>
    /// <paramref name="owner"/>'s personal memory space called <paramref name="name"/>, made now when it doesn't exist
    /// yet. Throws <see cref="ArgumentException"/> when the name isn't a valid container name or the owner has no id.
    /// </summary>
    Task<Container> EnsurePersonalSpaceAsync(MemoryCaller owner, string name, string? description, CancellationToken ct = default);
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
