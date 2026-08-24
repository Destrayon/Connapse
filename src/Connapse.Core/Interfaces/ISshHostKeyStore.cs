namespace Connapse.Core.Interfaces;

/// <summary>
/// Records the host key fingerprint a connection is pinned to.
/// <para>
/// A seam, rather than handing <see cref="IConnectionStore"/> to the connector. A connector
/// is built fresh on every sync cycle and is otherwise a read-only view of somebody else's
/// system; giving it the ability to rewrite arbitrary connection fields to support one
/// write is a much wider grant than the write needs.
/// </para>
/// </summary>
public interface ISshHostKeyStore
{
    /// <summary>
    /// Pins <paramref name="fingerprint"/> to the connection, first use only.
    /// </summary>
    /// <remarks>
    /// Called after the session is fully established — <b>after</b> authentication, not from
    /// the host-key callback. A key presented by a server we then failed to authenticate to
    /// is not a key worth pinning, and pinning it would make a hostile server's key the one
    /// every later connection is compared against.
    /// <para>
    /// Implementations must not overwrite a fingerprint that is already recorded. The
    /// mismatch path refuses the connection rather than reaching here, so an arriving write
    /// against an existing value means two cycles raced, and the recorded value wins.
    /// </para>
    /// </remarks>
    Task RecordFingerprintAsync(Guid connectionId, string fingerprint, CancellationToken ct = default);
}
