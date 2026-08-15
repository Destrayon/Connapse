namespace Connapse.Core.Interfaces;

/// <summary>
/// The result of one incremental sync call.
/// <para>
/// <c>RequiresFullResync</c> is not an error signal. The strongest provider APIs answer a
/// stale delta token with an explicit "start over" — Microsoft Graph returns HTTP 410 Gone,
/// Dropbox a 409 reset — and ignoring that produces a corpus that silently drifts out of
/// date. When it is set, the caller must clear the stored cursor and re-list from scratch,
/// which is why a resync response carries no <c>NextCursor</c>.
/// </para>
/// </summary>
public record SyncDelta(
    IReadOnlyList<ConnectorFile> Upserted,
    IReadOnlyList<string> DeletedPaths,
    string? NextCursor,
    bool RequiresFullResync);

/// <summary>
/// A connector that can report what changed since a durable cursor, rather than requiring
/// the whole remote corpus to be listed and diffed on every poll.
/// <para>
/// Deliberately optional, and deliberately read-only. S3 and Azure Blob have no delta API
/// and do not implement it; the sync engine falls back to list-and-diff for those.
/// Implement it only where the provider genuinely offers one.
/// </para>
/// </summary>
public interface ISyncCursorConnector : IConnector
{
    /// <summary>
    /// Returns changes since <paramref name="cursor"/>, or the initial set when it is null.
    /// </summary>
    Task<SyncDelta> GetChangesAsync(string? cursor, CancellationToken ct = default);
}
