namespace Connapse.Core.Utilities;

/// <summary>
/// Resolves a remote path so a caller can decide whether it escapes a root, using the
/// server's own canonicalisation.
/// </summary>
/// <remarks>
/// Deliberately an interface in Core rather than SSH.NET's own type: Core has no external
/// dependencies, and the seam is what lets the confinement rules be tested without standing
/// up a server.
/// </remarks>
public interface ISftpRealPathResolver
{
    /// <summary>
    /// Canonicalises <paramref name="path"/> on the remote server via
    /// <c>SSH_FXP_REALPATH</c>, resolving <c>..</c> segments and symlinks there.
    /// Throws when the server refuses or the session is unusable.
    /// </summary>
    string GetCanonicalPath(string path);
}

/// <summary>
/// Confines a remote path beneath an SFTP connection's allowed root.
/// <para>
/// A separate implementation from <see cref="PathConfinement"/>, and it has to be.
/// <see cref="PathConfinement"/> calls <c>Directory.Exists</c>, <c>File.Exists</c> and
/// <c>FileSystemInfo.ResolveLinkTarget</c> — local filesystem I/O. Handed a remote path
/// those either return false or resolve something on the machine running Connapse, which
/// silently degrades confinement to a lexical prefix check. That is precisely the bug #365
/// fixed for local paths, and reusing the local helper here would reintroduce it remotely.
/// </para>
/// <para>
/// So resolution happens on the server, through <c>SSH_FXP_REALPATH</c>: <c>..</c> segments
/// and symlinks collapse where they actually mean something, and only the resolved result is
/// compared.
/// </para>
/// </summary>
public static class SftpPathConfinement
{
    /// <summary>
    /// SFTP paths are POSIX-shaped regardless of the server's operating system. Windows
    /// OpenSSH presents drives as <c>/C:/Users/...</c> — a leading slash before the drive
    /// letter — rather than switching to backslashes.
    /// </summary>
    public const char Separator = '/';

    /// <summary>
    /// Combines <paramref name="relative"/> beneath <paramref name="root"/>, canonicalises
    /// both on the server, and returns the resolved path when it stays inside the root — or
    /// null when it escapes, when the input is unusable, or when the server will not resolve
    /// it.
    /// </summary>
    public static string? CombineWithin(ISftpRealPathResolver resolver, string root, string? relative)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (string.IsNullOrWhiteSpace(relative))
            return ResolveWithin(resolver, root, root);

        // An absolute relative path is refused rather than honoured. This mirrors
        // PathConfinement.CombineWithin: treating it as a path under the root would hand back
        // somewhere else on the server entirely.
        if (relative.StartsWith(Separator))
            return null;

        string joined = root.TrimEnd(Separator) + Separator + relative;

        return ResolveWithin(resolver, root, joined);
    }

    /// <summary>
    /// Returns the server-resolved <paramref name="candidate"/> when it lies inside
    /// <paramref name="root"/>, or null when it escapes. Both sides are canonicalised, so a
    /// root that is itself a symlink cannot make a contained path look like an escape.
    /// </summary>
    public static string? ResolveWithin(ISftpRealPathResolver resolver, string root, string candidate)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(candidate);

        string canonicalRoot;
        string canonicalCandidate;

        try
        {
            canonicalRoot = resolver.GetCanonicalPath(root);
            canonicalCandidate = resolver.GetCanonicalPath(candidate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A path the server will not resolve is a path we cannot vouch for. Refusing is
            // the only safe direction: returning the unresolved string would let the
            // comparison below pass on something nobody verified.
            return null;
        }

        if (string.IsNullOrEmpty(canonicalRoot) || string.IsNullOrEmpty(canonicalCandidate))
            return null;

        return IsWithin(canonicalRoot, canonicalCandidate) ? canonicalCandidate : null;
    }

    /// <summary>
    /// True when <paramref name="canonicalCandidate"/> is <paramref name="canonicalRoot"/>
    /// itself or sits beneath it. Both arguments must already be server-canonicalised.
    /// </summary>
    /// <remarks>
    /// Compared ordinally, including against a Windows OpenSSH server where the underlying
    /// filesystem is case-insensitive. The failure modes are not symmetric: comparing too
    /// strictly refuses a legitimate path, which is visible and fixable, while comparing too
    /// loosely admits a path that escapes the root, which is silent. There is no reliable way
    /// to learn a remote server's case rules over SFTP, so this takes the visible failure.
    /// </remarks>
    public static bool IsWithin(string canonicalRoot, string canonicalCandidate)
    {
        string trimmedRoot = canonicalRoot.TrimEnd(Separator);

        // A trailing separator on the prefix test, so a sibling sharing a name prefix cannot
        // pass: "/data-other" starts with "/data" as a string but is not inside it. This is
        // the nginx `alias` off-by-slash bug, and the most common way this check is written
        // wrong.
        string rootWithSep = trimmedRoot + Separator;

        // A root that canonicalises to "/" trims to empty, which would make rootWithSep "/"
        // and match everything — correct, since everything genuinely is beneath the server
        // root, but only reached when an administrator configured "/" as the allowed root.
        return canonicalCandidate.Equals(trimmedRoot, StringComparison.Ordinal)
            || canonicalCandidate.StartsWith(rootWithSep, StringComparison.Ordinal);
    }
}
