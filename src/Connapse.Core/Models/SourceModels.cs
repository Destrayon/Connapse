namespace Connapse.Core;

public enum SyncStatus { Never = 0, Running = 1, Succeeded = 2, Failed = 3 }

public record Source(
    Guid Id,
    string Name,
    string? Description,

    // Nullable: a connection-bound source sets ConnectionId and leaves Provider null
    // (the provider is derived from the Connection); a connection-less source (e.g.
    // public GitHub, epic #508) sets Provider and leaves ConnectionId null.
    Guid? ConnectionId,
    string ScopeJson,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool Enabled = true,
    string? SyncCursor = null,
    DateTime? LastSyncedAt = null,
    SyncStatus LastSyncStatus = SyncStatus.Never,
    string? LastSyncError = null,
    int? SyncIntervalSeconds = null,
    ContainerSettingsOverrides? SettingsOverrides = null,
    string? Summary = null,
    DateTime? SummaryGeneratedAt = null,
    string? SummaryDocSetHash = null,
    int DocumentCount = 0,
    int? WithheldDeletions = null,

    // Appended rather than inserted: this record is constructed positionally in places, so a
    // parameter added anywhere else shifts every argument after it.
    //
    // Counted separately because DocumentCount includes every row whatever its status, so a
    // source where nothing could be embedded still reported its files and a green badge. The
    // sync did succeed — it listed and enqueued correctly — and the failure downstream had no
    // way back to the page (#400).
    int FailedDocumentCount = 0,

    // See the ConnectionId comment above: null for a connection-bound source, set for a
    // connection-less one.
    ConnectionProvider? Provider = null,

    // When the sync engine started holding this source's cursor because a change arrived for a
    // document still being ingested. Null when it is not holding.
    DateTime? SyncHeldSince = null,

    // When the remote last refused this source's reads (a public repository gone private).
    // While set, the source's documents are kept out of search. Null when readable.
    DateTime? AccessRevokedAt = null);

/// <summary>
/// The remote refused to let this source read it any more — a public repository made private,
/// renamed away, or deleted. Distinct from an outage: the content Connapse already indexed was
/// public and no longer is, so the sync engine hides the source's documents from search until a
/// read succeeds again, rather than only recording a failed cycle.
/// </summary>
public class SourceAccessRevokedException(string message, Exception inner) : IOException(message, inner);

public record CreateSourceRequest(
    string Name,
    Guid? ConnectionId,
    string ScopeJson,
    string? Description = null,
    int? SyncIntervalSeconds = null,
    ConnectionProvider? Provider = null);

public record UpdateSourceRequest(
    string? Name = null,
    string? Description = null,
    string? ScopeJson = null,
    int? SyncIntervalSeconds = null,
    bool? Enabled = null);
