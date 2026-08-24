using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Connapse.Storage.Connectors;

/// <summary>
/// Read-only <see cref="IConnector"/> over SFTP.
/// <para>
/// The transport that answers "Connapse cannot see my files": the local filesystem connector
/// needs the server process to share a disk with the data, which a container or a hosted
/// deployment does not.
/// </para>
/// <para>
/// <c>SupportsLiveWatch = false</c> — SFTP has no change notification of any kind, so this is
/// list-and-diff on every poll. At a hundred thousand files that is minutes per scan, which
/// is why the documentation says to point a source at a folder rather than a whole drive.
/// </para>
/// </summary>
public sealed class SftpConnector : IConnector, IDisposable
{
    private readonly SftpConnectorConfig _config;
    private readonly ISshHostKeyStore? _hostKeyStore;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly Regex[] _include;
    private readonly Regex[] _exclude;

    private SftpClient? _client;

    /// <summary>The confined, server-resolved path this source reads from.</summary>
    private string? _resolvedRoot;

    /// <summary>
    /// Set from the host key callback, read after the session is established. The callback
    /// itself cannot record anything: it fires before authentication, and a key from a server
    /// we then fail to authenticate to is not one to pin.
    /// </summary>
    private string? _observedFingerprint;

    public SftpConnector(SftpConnectorConfig config, ISshHostKeyStore? hostKeyStore = null)
    {
        _config = config;
        _hostKeyStore = hostKeyStore;
        _include = CompileGlobs(config.IncludePatterns);
        _exclude = CompileGlobs(config.ExcludePatterns);
    }

    public ConnectorType Type => ConnectorType.Sftp;

    public bool SupportsLiveWatch => false;

    internal SftpConnectorConfig Config => _config;

    /// <summary>
    /// Remote paths are already absolute and server-canonical by the time they leave
    /// <see cref="ListFilesAsync"/>, so there is nothing to recombine.
    /// </summary>
    public string ResolveJobPath(string relativePath) => relativePath;

    public IAsyncEnumerable<ConnectorFileEvent> WatchAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("SFTP has no change notification; this connector is list-and-diff only.");

    public async Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(
        string? prefix = null, CancellationToken ct = default)
    {
        var (client, root) = await EnsureConnectedAsync(ct);

        string start = string.IsNullOrWhiteSpace(prefix)
            ? root
            : await ConfineDirectoryAsync(client, prefix, ct)
              ?? throw new UnauthorizedAccessException(
                  $"Prefix '{prefix}' resolves outside the connection's allowed root.");

        var files = new List<ConnectorFile>();
        await WalkAsync(client, start, files, ct);
        return files;
    }

    public async Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
    {
        var (client, _) = await EnsureConnectedAsync(ct);

        string confined = await ConfineFileAsync(client, path, ct)
            ?? throw new UnauthorizedAccessException(
                $"Path '{path}' resolves outside the connection's allowed root.");

        await RefuseIfLinkAsync(client, confined, ct);

        return await client.OpenAsync(confined, FileMode.Open, FileAccess.Read, ct);
    }

    /// <summary>
    /// Opens the session, verifies the host key, and resolves the allowed root and subpath on
    /// the server. Returns the resolved path this connector would read from.
    /// </summary>
    /// <remarks>
    /// Exists for the connection test, which needs exactly the three things that go wrong —
    /// reachability, authentication, and whether the root is really there — and none of the
    /// walking. An explicit method rather than leaning on a side effect of
    /// <see cref="ExistsAsync"/>, so a later change to that method cannot quietly turn the test
    /// button into one that always passes.
    /// </remarks>
    public async Task<string> ProbeAsync(CancellationToken ct = default)
    {
        var (_, root) = await EnsureConnectedAsync(ct);
        return root;
    }

    /// <summary>
    /// Refuses a path whose final component is a symlink.
    /// </summary>
    /// <remarks>
    /// Confinement canonicalises the <i>parent</i> and reattaches the name, so a link sitting at
    /// the leaf passes every check made so far — the parent is inside the root and the name
    /// carries no traversal — and <c>OpenAsync</c> would then follow it wherever it points.
    /// <para>
    /// Every leaf link is refused, not only those pointing outside. The walk already steps over
    /// links rather than following them, so no path the ingestion queue holds is ever a link,
    /// and nothing legitimate asks to read one. Resolving them here would make the read path
    /// follow what the listing deliberately does not — two different answers to the same
    /// question, which is how the local connector's #365 bug came about.
    /// </para>
    /// <para>
    /// Asked of the <b>parent's listing</b>, not of the path. <c>GetAttributes</c> looks like
    /// the obvious call and is the wrong one: SSH.NET canonicalises the path before issuing
    /// LSTAT, so for a link it returns the <i>target's</i> attributes and reports
    /// <c>IsSymbolicLink</c> as false. Verified against a real server — the first version of
    /// this method used it and both link tests still passed through. A directory listing does
    /// not follow links, which is why the walk can already tell them apart.
    /// </para>
    /// <para>
    /// Costs one listing per read. Still check-then-open, so a link planted in the instant
    /// between the two wins; closing that needs an anchored open, which SFTP has no verb for.
    /// The same limit <see cref="PathConfinement"/> documents for local paths.
    /// </para>
    /// </remarks>
    private static async Task RefuseIfLinkAsync(
        SftpClient client, string confinedPath, CancellationToken ct)
    {
        int lastSlash = confinedPath.LastIndexOf(SftpPathConfinement.Separator);
        string parent = lastSlash <= 0 ? "/" : confinedPath[..lastSlash];
        string name = confinedPath[(lastSlash + 1)..];

        ISftpFile? entry;
        try
        {
            entry = await client.ListDirectoryAsync(parent, ct)
                .FirstOrDefaultAsync(f => f.Name == name, ct);
        }
        catch (Exception ex) when (ex is SshException or IOException)
        {
            // A path we cannot inspect is a path we cannot vouch for.
            throw new UnauthorizedAccessException(
                $"Could not verify that '{confinedPath}' is an ordinary file.", ex);
        }

        if (entry is null)
            throw new FileNotFoundException($"File not found at '{confinedPath}'.", confinedPath);

        if (entry.IsSymbolicLink)
            throw new UnauthorizedAccessException(
                $"'{confinedPath}' is a symbolic link. Links are not followed, because the "
                + "listing that produced this path steps over them.");
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var (client, _) = await EnsureConnectedAsync(ct);

        string? confined = await ConfineFileAsync(client, path, ct);
        if (confined is null) return false;

        return await client.ExistsAsync(confined, ct);
    }

    // ── Walking ────────────────────────────────────────────────────────────

    /// <summary>
    /// Recursive listing that refuses to be partial.
    /// </summary>
    /// <remarks>
    /// A directory that cannot be read <b>fails the whole listing</b> rather than contributing
    /// nothing to it. This is the point worth being deliberate about: to the reconcile, a
    /// quietly-short listing is indistinguishable from a mass deletion, so swallowing a
    /// per-directory permission error is how a tightened ACL on one subtree turns into a
    /// silent deletion of everything under it. The delete guard bounds that damage; it does
    /// not prevent it, and it should not have to.
    /// </remarks>
    private async Task WalkAsync(SftpClient client, string directory, List<ConnectorFile> into, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        IEnumerable<ISftpFile> entries;
        try
        {
            entries = await client.ListDirectoryAsync(directory, ct).ToListAsync(ct);
        }
        catch (Exception ex) when (ex is SshException or IOException)
        {
            throw new IOException(
                $"Could not list '{directory}' on {_config.Host}. Refusing to return a partial "
                + "listing, because a short listing is indistinguishable from a mass deletion.", ex);
        }

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.Name is "." or "..")
                continue;

            // Links are skipped rather than followed, the same way the local connector skips
            // reparse points (#365). Following one is the escape route out of the root, and
            // resolving each through SSH_FXP_REALPATH would be a round trip per entry — real
            // cost on a large tree, to reach a target that is either outside the root and must
            // be refused, or inside it and already reached by the ordinary walk.
            if (entry.IsSymbolicLink)
                continue;

            if (entry.IsDirectory)
            {
                await WalkAsync(client, entry.FullName, into, ct);
                continue;
            }

            if (!entry.IsRegularFile)
                continue;

            if (!MatchesFilters(entry.Name))
                continue;

            into.Add(new ConnectorFile(
                Path: entry.FullName,
                SizeBytes: entry.Length,
                LastModified: entry.LastWriteTimeUtc,
                ContentType: null));
        }
    }

    private bool MatchesFilters(string fileName)
    {
        if (_include.Length > 0 && !_include.Any(r => Matches(r, fileName)))
            return false;

        return !_exclude.Any(r => Matches(r, fileName));
    }

    /// <summary>
    /// A pattern that times out is treated as not matching, and logged nowhere because there is
    /// nothing to log to from here — but it must not take the sync down. Withholding a match is
    /// the conservative direction for an include list; for an exclude list it means indexing a
    /// file that was meant to be skipped, which is visible and recoverable.
    /// </summary>
    private static bool Matches(Regex regex, string fileName)
    {
        try
        {
            return regex.IsMatch(fileName);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Glob patterns compiled once per connector rather than once per file per pattern.
    /// </summary>
    /// <remarks>
    /// This connector is documented as handling trees of a hundred thousand files, and the old
    /// code built a fresh <see cref="Regex"/> inside the walk — so a source with three patterns
    /// compiled three hundred thousand regexes per cycle.
    /// <para>
    /// The timeout matters more than the caching. <see cref="Regex.Escape"/> leaves the
    /// substituted <c>.*</c> live, so a pattern like <c>*a*a*a*a*a*b</c> becomes nested
    /// wildcards and backtracks catastrophically against a long run of <c>a</c>. Patterns come
    /// from a source's scope, so one bad entry would hang that source's sync thread on every
    /// cycle, for ever, with no way to see why.
    /// </para>
    /// </remarks>
    private static Regex[] CompileGlobs(IReadOnlyList<string> patterns) =>
        [.. patterns.Select(p => new Regex(
            "^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            GlobMatchTimeout))];

    private static readonly TimeSpan GlobMatchTimeout = TimeSpan.FromMilliseconds(250);

    // ── Confinement ────────────────────────────────────────────────────────

    private Task<string?> ConfineDirectoryAsync(SftpClient client, string candidate, CancellationToken ct) =>
        SftpPathConfinement.ResolveWithinAsync(
            new SftpRealPathResolver(client), _config.AllowedRoot, candidate, ct);

    /// <summary>
    /// Confines a file path by canonicalising its <i>parent</i> and reattaching the name.
    /// </summary>
    /// <remarks>
    /// The server's <c>realpath</c> is only reachable through <c>ChangeDirectory</c> in this
    /// library, and that only accepts directories. Canonicalising the parent is not a
    /// weakening: every way out of the root runs through a directory — a <c>..</c> segment or
    /// a symlinked ancestor — and both of those are in the parent. The leaf is checked for
    /// separators instead, so it cannot smuggle a path component of its own.
    /// </remarks>
    private async Task<string?> ConfineFileAsync(SftpClient client, string candidate, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        int lastSlash = candidate.LastIndexOf(SftpPathConfinement.Separator);
        if (lastSlash <= 0)
            return null;

        string parent = candidate[..lastSlash];
        string name = candidate[(lastSlash + 1)..];

        // A name that is empty, a traversal, or carries its own separator is not a file name.
        if (name.Length == 0 || name is "." or ".." || name.Contains(SftpPathConfinement.Separator))
            return null;

        string? resolvedParent = await ConfineDirectoryAsync(client, parent, ct);
        if (resolvedParent is null)
            return null;

        return resolvedParent.TrimEnd(SftpPathConfinement.Separator)
            + SftpPathConfinement.Separator + name;
    }

    // ── Session ────────────────────────────────────────────────────────────

    private async Task<(SftpClient Client, string Root)> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client is { IsConnected: true } live && _resolvedRoot is not null)
            return (live, _resolvedRoot);

        await _connectGate.WaitAsync(ct);
        try
        {
            if (_client is { IsConnected: true } stillLive && _resolvedRoot is not null)
                return (stillLive, _resolvedRoot);

            _client?.Dispose();
            _client = null;
            _resolvedRoot = null;

            var client = new SftpClient(BuildConnectionInfo())
            {
                // Without this SSH.NET waits forever on an SFTP request. See the config's
                // remarks: a cancellation token does not reach inside one.
                OperationTimeout = _config.OperationTimeout,
            };
            client.HostKeyReceived += OnHostKeyReceived;

            try
            {
                await client.ConnectAsync(ct);
            }
            catch (Exception ex)
            {
                client.Dispose();

                // Refusing the key makes SSH.NET fail the handshake with a message about key
                // exchange, which tells an operator nothing about what actually happened or
                // what to do. Replace it with the one that names both fingerprints.
                string? refusal = DescribeHostKeyRefusalIfAny();
                throw refusal is null
                    ? ex
                    : new SftpHostKeyMismatchException(refusal, ex);
            }

            // Everything from here to the assignment can throw, and the client is already
            // connected — so it has to be disposed on the way out or the session is orphaned.
            //
            // Not a theoretical leak. SourceSyncService builds a fresh connector every cycle
            // and disposes it in a finally, but Dispose reads _client, which is still null
            // until the assignment below. A source with a root that does not resolve, or a
            // subPath that escapes it, would leak one SSH session and one socket every five
            // minutes until the server hit MaxSessions and started refusing everyone —
            // including every other source pointed at the same machine.
            try
            {
                // Only now, with authentication behind us. A fingerprint captured in the
                // callback and recorded there would pin whatever answered the address,
                // authenticated or not.
                await PinFingerprintIfNewAsync(ct);

                string root = await ConfineDirectoryAsync(client, _config.AllowedRoot, ct)
                    ?? throw new InvalidOperationException(
                        $"The allowed root '{_config.AllowedRoot}' could not be resolved on {_config.Host}.");

                string scoped = await SftpPathConfinement.CombineWithinAsync(
                                    new SftpRealPathResolver(client), root, _config.SubPath, ct)
                                ?? throw new InvalidOperationException(
                                    $"The source's subPath '{_config.SubPath}' resolves outside the "
                                    + $"connection's allowed root '{_config.AllowedRoot}'.");

                _client = client;
                _resolvedRoot = scoped;
                return (client, scoped);
            }
            catch
            {
                client.HostKeyReceived -= OnHostKeyReceived;
                client.Dispose();
                throw;
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private ConnectionInfo BuildConnectionInfo()
    {
        var credential = _config.Credential
            ?? throw new InvalidOperationException(
                $"The SFTP connection for '{_config.Host}' has no private key stored.");

        PrivateKeyFile key;
        using (var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(credential.PrivateKey)))
        {
            key = string.IsNullOrWhiteSpace(credential.Passphrase)
                ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, credential.Passphrase);
        }

        return new ConnectionInfo(
            _config.Host,
            _config.Port,
            _config.Username,
            new PrivateKeyAuthenticationMethod(_config.Username, key))
        {
            Timeout = _config.OperationTimeout,
        };
    }

    /// <summary>
    /// The callback SSH.NET raises before authentication. Handling it is not optional:
    /// <see cref="HostKeyEventArgs.CanTrust"/> arrives already set to <c>true</c>, so a
    /// connector that does not subscribe accepts every key any server presents, and looks
    /// from the outside exactly like one that verifies.
    /// </summary>
    private void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        string presented = SshHostKeyPolicy.FormatFingerprint(SHA256.HashData(e.HostKey));
        _observedFingerprint = presented;

        switch (SshHostKeyPolicy.Evaluate(_config.PinnedHostKeyFingerprint, presented))
        {
            case SshHostKeyDecision.Matches:
            case SshHostKeyDecision.TrustOnFirstUse:
                e.CanTrust = true;
                break;

            default:
                e.CanTrust = false;
                break;
        }
    }

    private async Task PinFingerprintIfNewAsync(CancellationToken ct)
    {
        if (_hostKeyStore is null
            || _config.ConnectionId == Guid.Empty
            || _observedFingerprint is null
            || !string.IsNullOrWhiteSpace(_config.PinnedHostKeyFingerprint))
            return;

        await _hostKeyStore.RecordFingerprintAsync(_config.ConnectionId, _observedFingerprint, ct);
    }

    /// <summary>
    /// A refused host key surfaces from SSH.NET as a connection failure whose message does not
    /// say why. This turns it into one that names both fingerprints and the fix.
    /// </summary>
    public string? DescribeHostKeyRefusalIfAny()
    {
        if (_observedFingerprint is null || string.IsNullOrWhiteSpace(_config.PinnedHostKeyFingerprint))
            return null;

        return SshHostKeyPolicy.Evaluate(_config.PinnedHostKeyFingerprint, _observedFingerprint)
            is SshHostKeyDecision.Mismatch
            ? SshHostKeyPolicy.DescribeMismatch(
                _config.Host, _config.PinnedHostKeyFingerprint, _observedFingerprint)
            : null;
    }

    public void Dispose()
    {
        if (_client is not null)
        {
            _client.HostKeyReceived -= OnHostKeyReceived;
            _client.Dispose();
            _client = null;
        }

        _connectGate.Dispose();
    }
}

/// <summary>
/// The server presented a host key that does not match the one pinned to the connection.
/// <para>
/// Its own type rather than a bare <see cref="InvalidOperationException"/>, because this is
/// the one sync failure that is never transient: retrying cannot fix it, and an operator has
/// to decide whether they performed the rekey themselves.
/// </para>
/// </summary>
public class SftpHostKeyMismatchException(string message, Exception inner)
    : Exception(message, inner);

/// <summary>
/// Adapts SSH.NET to <see cref="ISftpRealPathResolver"/>.
/// </summary>
/// <remarks>
/// <c>SSH_FXP_REALPATH</c> has no public entry point in SSH.NET — <c>ISftpSession</c> keeps
/// <c>GetCanonicalPath</c> internal. <c>ChangeDirectory</c> is the way to reach it: it issues
/// the realpath request and leaves the resolved answer in <c>WorkingDirectory</c>. The design
/// note that named <c>SftpClient.GetCanonicalPath</c> was wrong about the library's surface.
/// <para>
/// The consequence is that only directories can be canonicalised, which is why file paths are
/// confined through their parent.
/// </para>
/// </remarks>
internal sealed class SftpRealPathResolver(SftpClient client) : ISftpRealPathResolver
{
    public async Task<string> GetCanonicalPathAsync(string path, CancellationToken ct = default)
    {
        await client.ChangeDirectoryAsync(path, ct);
        return client.WorkingDirectory;
    }
}

