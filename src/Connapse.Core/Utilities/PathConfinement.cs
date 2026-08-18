namespace Connapse.Core.Utilities;

/// <summary>
/// Confines a path beneath a root directory, for connectors that mirror a local filesystem.
/// <para>
/// The root is a security boundary an administrator configured: a source may name a subpath
/// inside it and must not be able to reach past it. Getting this right needs more than
/// <see cref="Path.GetFullPath(string)"/>, which is purely lexical and never touches the
/// filesystem — so it silently admits a symlink or NTFS junction that points elsewhere
/// (CWE-59). PHP's <c>open_basedir</c> resolves links before its check for the same reason,
/// and Java's <c>getCanonicalPath</c> does it as a matter of course; .NET requires
/// <see cref="FileSystemInfo.ResolveLinkTarget(bool)"/> explicitly.
/// </para>
/// <para>
/// What this cannot defend against: bind mounts and hard links, where the resolved path
/// genuinely is beneath the root and there is no string left to inspect. Those need a
/// low-privilege service account and a deployment that keeps the application's own
/// configuration and DataProtection key ring outside every configured root.
/// </para>
/// </summary>
public static class PathConfinement
{
    /// <summary>
    /// Case-insensitive only on Windows, where NTFS is reliably case-insensitive and an
    /// ordinal comparison would wrongly read <c>C:\Data\x</c> as outside <c>C:\data</c>.
    /// <para>
    /// Everywhere else this compares ordinally, macOS included. APFS is case-insensitive by
    /// default but can be formatted case-sensitive, and on such a volume <c>/Data</c> and
    /// <c>/data</c> are genuinely different directories — matching them loosely would admit
    /// one as being inside the other. The failure modes are not symmetric: comparing too
    /// strictly rejects a legitimate path, which is visible and fixable, while comparing too
    /// loosely admits a path that escapes the root, which is silent.
    /// </para>
    /// </summary>
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Returns the fully resolved <paramref name="candidate"/> when it lies inside
    /// <paramref name="root"/>, or null when it escapes. Both are resolved through any
    /// symlinks before comparison.
    /// </summary>
    public static string? ResolveWithin(string root, string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(candidate);

        string? rootFull;
        string? candidateFull;

        try
        {
            rootFull = ResolveLinks(Path.GetFullPath(root));
            candidateFull = ResolveLinks(Path.GetFullPath(candidate));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            // Path.GetFullPath rejects malformed input. A path we cannot even normalize is
            // not a path we can vouch for.
            return null;
        }

        // Either side failing to resolve means the comparison would be against an unverified
        // string, so refuse rather than guess.
        if (rootFull is null || candidateFull is null)
            return null;

        return IsWithin(rootFull, candidateFull) ? candidateFull : null;
    }

    /// <summary>
    /// Combines <paramref name="relative"/> beneath <paramref name="root"/> and confines the
    /// result. A rooted <paramref name="relative"/> is rejected rather than honoured:
    /// <see cref="Path.Combine(string, string)"/> discards the first argument entirely when
    /// the second is absolute, so treating it as a path under the root would silently hand
    /// back somewhere else on disk.
    /// </summary>
    public static string? CombineWithin(string root, string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(relative);

        if (Path.IsPathRooted(relative))
            return null;

        return ResolveWithin(root, Path.Combine(Path.GetFullPath(root), relative.TrimStart('/', '\\')));
    }

    /// <summary>
    /// True when <paramref name="fullCandidate"/> is <paramref name="fullRoot"/> itself or
    /// sits beneath it. Both arguments must already be fully resolved.
    /// </summary>
    public static bool IsWithin(string fullRoot, string fullCandidate)
    {
        // Compared with a trailing separator, so a sibling sharing a name prefix cannot pass:
        // "/data-other" starts with "/data" as a string but is not inside it. This is the
        // nginx `alias` off-by-slash bug, and it is the most common way this check is written
        // wrong.
        string rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        return fullCandidate.Equals(fullRoot.TrimEnd(Path.DirectorySeparatorChar), PathComparison)
            || fullCandidate.StartsWith(rootWithSep, PathComparison);
    }

    /// <summary>
    /// True when the entry is a symlink, junction, or other reparse point. Enumeration must
    /// skip these rather than descend through them.
    /// </summary>
    public static bool IsLink(string path)
    {
        try
        {
            var info = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : (FileSystemInfo)new FileInfo(path);

            return info.LinkTarget is not null
                || info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable means untrustworthy: refuse it rather than assume it is ordinary.
            return true;
        }
    }

    /// <summary>
    /// Walks every link on the path back to its final target, resolving ancestors before the
    /// leaf.
    /// <para>
    /// Resolving only the leaf is not enough, and this is the subtle half of the bug: in
    /// <c>/root/link-to-elsewhere/keyring.txt</c> the file itself is not a link, so asking it
    /// for a link target returns nothing and the path looks contained. Only its parent gives
    /// the escape away. Each ancestor is therefore resolved first and the remainder rebuilt
    /// on top of the result.
    /// </para>
    /// <para>
    /// A path that does not exist resolves as far as its existing ancestors allow — a root may
    /// legitimately be configured before it is created.
    /// </para>
    /// <para>
    /// Returns null when resolution fails. Callers must treat that as "outside every root":
    /// a path we cannot resolve is a path we cannot vouch for.
    /// </para>
    /// </summary>
    private static string? ResolveLinks(string fullPath)
    {
        try
        {
            string? parent = Path.GetDirectoryName(fullPath);

            // A filesystem root (C:\ or /) has no parent left to resolve.
            if (string.IsNullOrEmpty(parent) || parent == fullPath)
                return fullPath;

            string? resolvedParent = ResolveLinks(parent);
            if (resolvedParent is null)
                return null;

            string here = Path.Combine(resolvedParent, Path.GetFileName(fullPath));

            if (Directory.Exists(here))
                return new DirectoryInfo(here).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? here;

            if (File.Exists(here))
                return new FileInfo(here).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? here;

            return here;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A path we cannot resolve is a path we cannot vouch for. Returning it unchanged
            // would let the caller's containment check pass on an unverified string, so the
            // caller is forced to refuse it instead.
            return null;
        }
    }
}
