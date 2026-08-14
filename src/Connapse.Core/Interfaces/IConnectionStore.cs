namespace Connapse.Core.Interfaces;

public interface IConnectionStore
{
    Task<Connection> CreateAsync(CreateConnectionRequest request, Guid? createdByUserId, CancellationToken ct = default);
    Task<Connection?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Connection>> ListAsync(int skip = 0, int take = 50, CancellationToken ct = default);
    Task<Connection?> UpdateAsync(Guid id, UpdateConnectionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Deletes a connection. Throws InvalidOperationException if any source still
    /// references it — sources must be removed or repointed first.
    /// </summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Decrypts and returns the stored secret. Only the sync engine and connection
    /// testers should call this; it is never surfaced through the Connection model.
    /// Returns null when the connection has no secret (for example Filesystem).
    /// Throws System.Security.Cryptography.CryptographicException if the stored
    /// ciphertext cannot be unprotected, which happens after DataProtection key loss.
    /// </summary>
    Task<string?> GetSecretAsync(Guid id, CancellationToken ct = default);
}
