namespace Connapse.Core;

/// <summary>
/// External provider a Connection authenticates to. Values match ConnectorType
/// so Phase 2 can backfill existing containers with a direct cast.
/// ManagedStorage is deliberately absent: it is Connapse's own backend, not an
/// external system requiring credentials.
/// </summary>
public enum ConnectionProvider { Filesystem = 1, S3 = 3, AzureBlob = 4 }

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
