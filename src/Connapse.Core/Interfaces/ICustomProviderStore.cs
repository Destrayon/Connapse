namespace Connapse.Core.Interfaces;

public interface ICustomProviderStore
{
    /// <summary>Throws <see cref="InvalidOperationException"/> when the name is taken.</summary>
    Task<CustomProvider> CreateAsync(SaveCustomProviderRequest request, CancellationToken ct = default);

    /// <summary>Null when there is no such provider. A null token keeps the stored one.</summary>
    Task<CustomProvider?> UpdateAsync(Guid id, SaveCustomProviderRequest request, CancellationToken ct = default);

    Task<CustomProvider?> GetAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<CustomProvider>> ListAsync(CancellationToken ct = default);

    /// <summary>The decrypted token, or null when there is none.</summary>
    Task<string?> GetTokenAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// False when there is no such provider. Throws <see cref="InvalidOperationException"/> while a connection
    /// uses it: the connection's sources would otherwise lose their permission checks.
    /// </summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}
