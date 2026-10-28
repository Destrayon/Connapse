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

    /// <summary>
    /// Records the outcome of a check of the connection's credential. A failure keeps the last
    /// success time, and the confirmed permissions, of the check before it. Returns what was stored,
    /// or null when the connection is gone.
    /// </summary>
    Task<ConnectionHealth?> RecordHealthAsync(Guid id, ConnectionHealth health, CancellationToken ct = default);

    /// <summary>
    /// The stored secret as <see cref="GetSecretAsync"/> returns it, read only if the connection is still at
    /// <paramref name="version"/> (its <see cref="Connection.UpdatedAt"/>), so the secret belongs to the settings that
    /// were read with it. Null when the connection is gone or has no secret. Throws
    /// <see cref="ConnectionChangedException"/> when it was saved since.
    /// </summary>
    Task<string?> GetSecretAtAsync(Guid id, DateTime version, CancellationToken ct = default);

    /// <summary>
    /// Records health as <see cref="RecordHealthAsync"/> does, only if the connection is still at
    /// <paramref name="version"/>: a check of settings that were replaced since says nothing about the new ones.
    /// Null when the connection is gone or changed.
    /// </summary>
    Task<ConnectionHealth?> RecordHealthAtAsync(Guid id, ConnectionHealth health, DateTime version, CancellationToken ct = default);
}
