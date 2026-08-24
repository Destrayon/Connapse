namespace Connapse.Core;

/// <summary>
/// External provider a Connection authenticates to. Values match ConnectorType
/// so Phase 2 can backfill existing containers with a direct cast.
/// ManagedStorage is deliberately absent: it is Connapse's own backend, not an
/// external system requiring credentials.
/// </summary>
public enum ConnectionProvider { Filesystem = 1, S3 = 3, AzureBlob = 4, Sftp = 5 }

public record Connection(
    Guid Id,
    string Name,
    ConnectionProvider Provider,
    string? ConfigJson,
    Guid? CreatedByUserId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool HasSecret = false,
    int SourceCount = 0);

public record CreateConnectionRequest(
    string Name,
    ConnectionProvider Provider,
    string? ConfigJson = null,
    string? Secret = null);

public record UpdateConnectionRequest(
    string? Name = null,
    string? ConfigJson = null,
    string? Secret = null);

/// <summary>
/// Thrown when a stored connection secret exists but cannot be decrypted — in practice,
/// because the DataProtection key ring that encrypted it is gone or unreachable (a replaced
/// volume, or a replica that does not share the key directory). Distinct from "no secret
/// stored", which returns null. Callers should surface a reconnect prompt rather than
/// treating this as a transient fault; retrying will not help.
/// </summary>
public class ConnectionSecretUnavailableException(Guid connectionId, Exception inner)
    : Exception($"The stored secret for connection '{connectionId}' could not be decrypted. " +
                "The DataProtection key ring that encrypted it is unavailable, so the credential " +
                "must be re-entered.", inner)
{
    public Guid ConnectionId { get; } = connectionId;
}
