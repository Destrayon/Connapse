namespace Connapse.Core.Interfaces;

/// <summary>
/// Persistent storage for runtime-mutable application settings.
/// Settings are stored by category and can be retrieved, updated, and reset.
/// </summary>
public interface ISettingsStore
{
    /// <summary>
    /// Retrieves settings for a specific category.
    /// </summary>
    /// <typeparam name="T">The settings type (e.g., EmbeddingSettings).</typeparam>
    /// <param name="category">The settings category identifier (e.g., "Embedding").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The settings object, or null if not found.</returns>
    Task<T?> GetAsync<T>(string category, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Saves settings for a specific category.
    /// </summary>
    /// <typeparam name="T">The settings type.</typeparam>
    /// <param name="category">The settings category identifier.</param>
    /// <param name="settings">The settings object to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAsync<T>(string category, T settings, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Resets settings for a specific category to defaults (removes from store).
    /// </summary>
    /// <param name="category">The settings category identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ResetAsync(string category, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all stored settings categories.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of category names.</returns>
    Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken cancellationToken = default);
}
