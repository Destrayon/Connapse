using Connapse.Core;

namespace Connapse.Application.Ports;

/// <summary>
/// Runs one sync cycle for a source now, rather than at its next scheduled poll. The sync loop
/// lives in Web, which answers this for the sync operation; the operation decides who may ask.
/// </summary>
public interface ISourceSync
{
    /// <summary>
    /// Syncs <paramref name="source"/> through <paramref name="connection"/> (null for a connection-less
    /// source). <paramref name="applyWithheldDeletions"/> applies deletions a previous cycle withheld.
    /// </summary>
    Task<SourceSyncResult> SyncNowAsync(Source source, Connection? connection, bool applyWithheldDeletions, CancellationToken ct = default);
}
