using Connapse.Core;

namespace Connapse.Web.Endpoints;

/// <summary>
/// What the API returns for a source.
/// <para>
/// Deliberately not the <see cref="Source"/> record. That carries <c>ScopeJson</c>, which
/// names buckets, prefixes and filesystem subpaths, and <c>SyncCursor</c>, an opaque
/// provider continuation token — infrastructure detail a reader has no reason to receive,
/// and the kind of thing that turns a read route into reconnaissance. Serializing the record
/// directly would hand both out.
/// </para>
/// </summary>
public record SourceResponse(
    Guid Id,
    string Name,
    string? Description,
    Guid ConnectionId,
    bool Enabled,
    SyncStatus LastSyncStatus,
    DateTime? LastSyncedAt,
    int? SyncIntervalSeconds,
    int DocumentCount,
    string? Summary,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    /// <summary>
    /// How many deletions the last reconcile declined to apply, or null when none are pending.
    /// Unlike <see cref="LastSyncError"/> this is not administrator-only: it is a count, and
    /// names nothing about the remote's contents or structure.
    /// </summary>
    int? WithheldDeletions,
    /// <summary>
    /// Populated for administrators only. A provider's failure text routinely echoes the
    /// thing that failed — "Access Denied for bucket payroll-data" — so returning it to
    /// every reader would give back exactly the infrastructure detail ScopeJson is withheld
    /// to protect.
    /// </summary>
    string? LastSyncError,
    /// <summary>
    /// Always "source". Present so a client consuming both this and the container routes can
    /// tell them apart on a single field, matching the MCP contract.
    /// </summary>
    string Kind = "source")
{
    public static SourceResponse From(Source source, bool includeDiagnostics) => new(
        Id: source.Id,
        Name: source.Name,
        Description: source.Description,
        ConnectionId: source.ConnectionId,
        Enabled: source.Enabled,
        LastSyncStatus: source.LastSyncStatus,
        LastSyncedAt: source.LastSyncedAt,
        SyncIntervalSeconds: source.SyncIntervalSeconds,
        DocumentCount: source.DocumentCount,
        Summary: source.Summary,
        CreatedAt: source.CreatedAt,
        UpdatedAt: source.UpdatedAt,
        WithheldDeletions: source.WithheldDeletions,
        LastSyncError: includeDiagnostics ? source.LastSyncError : null);
}
