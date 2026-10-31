using System.Collections;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Connapse.Eval.Systems;

/// <summary>
/// Identifies a built index, so a later run can restore it instead of indexing again (#672). Two runs
/// get the same key only when everything that shapes the index matches:
/// <list type="bullet">
/// <item>the datasets: name, version and file hashes, in order;</item>
/// <item>the config's index-time settings and chunking strategy (<see cref="SystemConfig.IndexKey"/>);</item>
/// <item><c>Knowledge__</c> environment variables that aren't search-time, since they override configs;</item>
/// <item>the content of the source that ingests documents: <c>src/**</c> except the search and agent
/// projects; the harness code that turns datasets into documents and uploads them (its Datasets, Model
/// and Systems folders); and the root build files. File contents rather than the git commit, so
/// uncommitted changes count too. Files git ignores don't, so what the host writes at runtime can't.</item>
/// </list>
/// </summary>
public static class IndexCacheKey
{
    /// <summary>Projects under <c>src/</c> that only run at search time; changing them keeps an index valid.</summary>
    private static readonly HashSet<string> SearchOnlyProjects = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connapse.Search", "Connapse.Agents",
    };

    private static readonly string[] RootBuildFiles = ["Directory.Build.props", "Directory.Packages.props", "global.json"];

    public static string Compute(
        SystemConfig config,
        IEnumerable<(string Name, string Version, IReadOnlyDictionary<string, string> FileHashes)> datasets,
        string sourceHash,
        IReadOnlyDictionary<string, string> environment)
    {
        StringBuilder canonical = new();
        canonical.Append("index=").Append(config.IndexKey).Append('\n');
        canonical.Append("source=").Append(sourceHash).Append('\n');
        foreach ((string name, string version, IReadOnlyDictionary<string, string> hashes) in datasets)
        {
            canonical.Append("dataset=").Append(name).Append('@').Append(version).Append('\n');
            foreach ((string file, string hash) in hashes.OrderBy(p => p.Key, StringComparer.Ordinal))
                canonical.Append("  ").Append(file).Append('=').Append(hash).Append('\n');
        }
        foreach ((string key, string value) in environment
                     .Where(p => p.Key.StartsWith("Knowledge__", StringComparison.OrdinalIgnoreCase)
                                 && !SystemConfig.IsSearchTime(p.Key.Replace("__", ":")))
                     .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            canonical.Append("env=").Append(key.ToLowerInvariant()).Append('=').Append(value).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))[..24];
    }

    /// <summary>The process's environment variables, as <see cref="Compute"/> takes them.</summary>
    public static IReadOnlyDictionary<string, string> CurrentEnvironment() =>
        Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => e.Value?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Folders the host writes while it runs (CON-153): the Data Protection key ring and connector state
    /// in <c>appdata</c>, and logs. Only the fallback walk needs this list; in a git work tree
    /// <c>.gitignore</c> already leaves them out.
    /// </summary>
    private static readonly HashSet<string> RuntimeFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "appdata", "logs", "uploads", "knowledge-data", "TestResults",
    };

    /// <summary>
    /// A content hash of the source that shapes an index, paths relative to <paramref name="repoRoot"/>.
    /// Files git ignores don't count, the way Nx and Turborepo pick task inputs, so what the host writes at
    /// runtime can't change the key; untracked files that aren't ignored do count.
    /// </summary>
    public static string SourceHash(string repoRoot)
    {
        List<string> roots = [];
        string src = Path.Combine(repoRoot, "src");
        if (Directory.Exists(src))
            roots.AddRange(Directory.EnumerateDirectories(src).Where(p => !SearchOnlyProjects.Contains(Path.GetFileName(p))));
        // Adapters decide each document's text and ID; Systems uploads them.
        roots.AddRange(new[] { "Datasets", "Model", "Systems" }
            .Select(folder => Path.Combine(repoRoot, "tests", "Connapse.Eval", folder))
            .Where(Directory.Exists));

        List<string> files = GitListedFiles(repoRoot, roots) ?? roots.SelectMany(SourceFiles).ToList();
        // .gitignore leaves out appsettings.Development.json, but the eval host loads it, so it can still
        // change how documents are chunked and embedded.
        files.AddRange(roots.SelectMany(SourceFiles)
            .Where(f => Path.GetFileName(f).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
                        && Path.GetExtension(f).Equals(".json", StringComparison.OrdinalIgnoreCase)));
        files.AddRange(RootBuildFiles.Select(f => Path.Combine(repoRoot, f)).Where(File.Exists));
        files = files.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToList();

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in files
                     .Select(f => (Full: f, Relative: Path.GetRelativePath(repoRoot, f).Replace('\\', '/')))
                     .OrderBy(f => f.Relative, StringComparer.Ordinal)
                     .Select(f => f.Full))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(repoRoot, file).Replace('\\', '/') + "\n"));
            hash.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// The tracked files under <paramref name="roots"/> plus untracked ones git doesn't ignore, or null when
    /// <paramref name="repoRoot"/> isn't a git work tree or git can't run. Files deleted from disk but not
    /// from the index are dropped.
    /// </summary>
    private static List<string>? GitListedFiles(string repoRoot, IReadOnlyList<string> roots)
    {
        if (roots.Count == 0)
            return [];
        ProcessStartInfo start = new("git")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string arg in new[] { "-c", "core.quotepath=off", "ls-files", "-z", "--cached", "--others", "--exclude-standard", "--" })
            start.ArgumentList.Add(arg);
        foreach (string root in roots)
            start.ArgumentList.Add(Path.GetRelativePath(repoRoot, root).Replace('\\', '/'));
        try
        {
            using Process git = Process.Start(start)!;
            git.ErrorDataReceived += (_, _) => { }; // drained so a chatty stderr can't block git
            git.BeginErrorReadLine();
            string output = git.StandardOutput.ReadToEnd();
            git.WaitForExit();
            if (git.ExitCode != 0)
                return null;
            // Paths come back relative to the working directory, repoRoot.
            return output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => Path.GetFullPath(Path.Combine(repoRoot, f)))
                .Where(File.Exists)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // git isn't installed
        }
    }

    private static IEnumerable<string> SourceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(directory, f).Replace('\\', '/').Split('/')[..^1].Any(RuntimeFolders.Contains));
}
