namespace Connapse.Core.Interfaces;

/// <summary>The queries admins saved for a container's tables (CON-311), by name, which is unique in the container.</summary>
public interface ISavedQueryStore
{
    /// <summary>The container's saved queries by name; empty when it has none or doesn't exist.</summary>
    Task<IReadOnlyList<SavedQuery>> ListAsync(Guid containerId, CancellationToken ct = default);

    Task<SavedQuery?> GetAsync(Guid containerId, string name, CancellationToken ct = default);

    /// <summary>Saves <paramref name="query"/>, replacing the container's query of the same name.</summary>
    Task<SavedQuery> SaveAsync(SavedQuery query, CancellationToken ct = default);

    /// <summary>False when the container has no query of that name.</summary>
    Task<bool> DeleteAsync(Guid containerId, string name, CancellationToken ct = default);
}
