namespace Connapse.AgentSearch.Prototype;

/// <summary>
/// The folder the tools may see. Every path an agent passes is resolved under it and refused if it
/// escapes, so `../` or an absolute path cannot read anything else on the machine.
/// </summary>
public sealed class FileRoot(string root, string ripgrep)
{
    public string Root { get; } = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    public string Ripgrep { get; } = ripgrep;

    /// <summary>Resolves an agent path like "/src/a.cs" or "src/a.cs"; null when it leaves the root.</summary>
    public string? Resolve(string? path)
    {
        string relative = (path ?? "").Replace('\\', '/').TrimStart('/');
        string full = Path.GetFullPath(Path.Combine(Root, relative));
        if (full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar == Root)
            return Root;
        return full.StartsWith(Root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>The agent-facing form of a full path: "/src/a.cs".</summary>
    public string Display(string full) =>
        "/" + Path.GetRelativePath(Root, full).Replace('\\', '/');
}
