namespace Connapse.AgentSearch.Prototype;

/// <summary>
/// The folder the tools may see. Every path an agent passes is resolved under it and refused if it
/// escapes, so `../` or an absolute path cannot read anything else on the machine.
/// </summary>
public sealed class FileRoot(string root, string ripgrep)
{
    public string Root { get; } = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    public string Ripgrep { get; } = ripgrep;

    // Windows and macOS file systems ignore case by default; Linux does not, so `/srv/Data/` must not admit `/srv/data/`.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Resolves an agent path like "/src/a.cs" or "src/a.cs"; null when it leaves the root.</summary>
    public string? Resolve(string? path)
    {
        string relative = (path ?? "").Replace('\\', '/').TrimStart('/');
        string full = Path.GetFullPath(Path.Combine(Root, relative));
        if (full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar == Root)
            return Root;
        if (!full.StartsWith(Root, PathComparison))
            return null;
        return PassesThroughLink(full) ? null : full;
    }

    // A symlink or junction inside the root can point anywhere, so any link on the way to the target is refused.
    private bool PassesThroughLink(string full)
    {
        string current = Root.TrimEnd(Path.DirectorySeparatorChar);
        foreach (string part in full[Root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return true;
        }
        return false;
    }

    /// <summary>The agent-facing form of a full path: "/src/a.cs".</summary>
    public string Display(string full) =>
        "/" + Path.GetRelativePath(Root, full).Replace('\\', '/');
}
