namespace Connapse.Core;

/// <summary>
/// External provider a Connection authenticates to. Values match ConnectorType
/// so Phase 2 can backfill existing containers with a direct cast.
/// ManagedStorage is deliberately absent: it is Connapse's own backend, not an
/// external system requiring credentials.
/// </summary>
public enum ConnectionProvider { Filesystem = 1, S3 = 3, AzureBlob = 4, Sftp = 5, GitHub = 6, Atlassian = 7, Custom = 8 }

public record Connection(
    Guid Id,
    string Name,
    ConnectionProvider Provider,
    string? ConfigJson,
    Guid? CreatedByUserId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool HasSecret = false,
    int SourceCount = 0,
    ConnectionHealth? Health = null);

/// <summary>
/// The outcome of the last check of a connection's credential, from a test or from a sync.
/// Never carries a secret or a token: <see cref="Message"/> is the text shown to an admin.
/// </summary>
/// <param name="CheckedAt">When the check ran (UTC).</param>
/// <param name="Succeeded">Every probe passed.</param>
/// <param name="Step">The probe that failed, or null when it passed.</param>
/// <param name="Message">What the check found, as shown to an admin.</param>
/// <param name="Origin">What ran the check: <c>test</c> or <c>sync</c>.</param>
/// <param name="CredentialRejected">The provider refused the stored credential: it needs replacing.</param>
/// <param name="Warning">The check could not finish for a reason that is not a fault (a site with no pages yet).</param>
/// <param name="LastSucceededAt">The last check that passed, carried forward by the store across failures.</param>
/// <param name="ConfirmedScopes">The permissions the last passing check confirmed the credential has.</param>
public record ConnectionHealth(
    DateTime CheckedAt,
    bool Succeeded,
    string? Step,
    string? Message,
    string Origin,
    bool CredentialRejected = false,
    bool Warning = false,
    DateTime? LastSucceededAt = null,
    IReadOnlyList<string>? ConfirmedScopes = null);

public record CreateConnectionRequest(
    string Name,
    ConnectionProvider Provider,
    string? ConfigJson = null,
    string? Secret = null)
{
    // The generated ToString would print the secret into any log line or message it reached.
    protected virtual bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Name = {Name}, Provider = {Provider}, ConfigJson = {ConfigJson}, Secret = {(Secret is null ? "null" : "***")}");
        return true;
    }
}

public record UpdateConnectionRequest(
    string? Name = null,
    string? ConfigJson = null,
    string? Secret = null)
{
    // The generated ToString would print the secret into any log line or message it reached.
    protected virtual bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Name = {Name}, ConfigJson = {ConfigJson}, Secret = {(Secret is null ? "null" : "***")}");
        return true;
    }
}

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
