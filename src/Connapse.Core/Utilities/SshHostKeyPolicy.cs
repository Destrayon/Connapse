namespace Connapse.Core.Utilities;

/// <summary>
/// What to do with the host key a server just presented.
/// </summary>
public enum SshHostKeyDecision
{
    /// <summary>Nothing is pinned yet. Trust it and record it — trust on first use.</summary>
    TrustOnFirstUse,

    /// <summary>The presented key matches what is pinned.</summary>
    Matches,

    /// <summary>The presented key differs from what is pinned. Refuse the connection.</summary>
    Mismatch
}

/// <summary>
/// Decides whether to trust an SSH server's host key, and formats fingerprints for storage
/// and display.
/// <para>
/// Pulled out of the connector as a pure function because getting it wrong is silent.
/// SSH.NET raises <c>HostKeyReceived</c> with <c>CanTrust</c> already set to <b>true</b>, so
/// a connector that simply does not subscribe accepts every key any server offers — the
/// insecure behaviour is the one you get by writing no code at all, and it looks identical
/// to the secure one from the outside.
/// </para>
/// <para>
/// The threat this addresses is interposition <i>after</i> a connection has been working:
/// somebody who can answer on the server's address later cannot silently take over a source
/// that has already synced. It does not authenticate the very first connection, which is the
/// standing trade-off of trust-on-first-use and the reason the recorded fingerprint is shown
/// on the connection for an administrator to check against <c>ssh-keyscan</c>.
/// </para>
/// </summary>
public static class SshHostKeyPolicy
{
    /// <summary>
    /// Compares a presented fingerprint against what is pinned for the connection.
    /// </summary>
    /// <param name="pinned">
    /// The recorded fingerprint, or null/blank when none has been recorded yet. Clearing it
    /// re-arms trust on first use, which is how a legitimate server rekey is handled.
    /// </param>
    public static SshHostKeyDecision Evaluate(string? pinned, string presented)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presented);

        if (string.IsNullOrWhiteSpace(pinned))
            return SshHostKeyDecision.TrustOnFirstUse;

        // Ordinal, and deliberately not a case-insensitive or whitespace-tolerant compare.
        // Both sides are produced by FormatFingerprint, so any difference at all is a
        // difference that should be surfaced rather than normalised away.
        return string.Equals(pinned.Trim(), presented.Trim(), StringComparison.Ordinal)
            ? SshHostKeyDecision.Matches
            : SshHostKeyDecision.Mismatch;
    }

    /// <summary>
    /// Formats a host key's SHA-256 hash the way OpenSSH does — <c>SHA256:</c> followed by
    /// unpadded base64 — so a recorded fingerprint can be compared by eye against
    /// <c>ssh-keyscan -t rsa host | ssh-keygen -lf -</c>.
    /// </summary>
    /// <param name="sha256Hash">The raw 32-byte SHA-256 of the host key blob.</param>
    public static string FormatFingerprint(ReadOnlySpan<byte> sha256Hash)
    {
        if (sha256Hash.IsEmpty)
            throw new ArgumentException("A host key hash cannot be empty.", nameof(sha256Hash));

        // OpenSSH prints the base64 without padding. Keeping the '=' would make a recorded
        // fingerprint fail a copy-paste comparison against ssh-keygen output for no reason.
        return "SHA256:" + Convert.ToBase64String(sha256Hash).TrimEnd('=');
    }

    /// <summary>
    /// The message shown when a pinned key stops matching. Names both fingerprints, because
    /// the operator's next step is deciding whether this was a rekey they performed or
    /// somebody answering on the server's address.
    /// </summary>
    public static string DescribeMismatch(string host, string pinned, string presented) =>
        $"The SSH host key for '{host}' does not match the one recorded for this connection. "
        + $"Expected {pinned}, but the server presented {presented}. "
        + "If the server was legitimately rekeyed, clear the recorded fingerprint on the "
        + "connection to trust the new key; otherwise the address is being answered by "
        + "something else.";
}
