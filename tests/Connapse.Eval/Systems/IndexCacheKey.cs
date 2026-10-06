using System.Collections;
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
/// projects, the harness's own systems code, and the root build files. File contents rather than the
/// git commit, so uncommitted changes count too.</item>
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

    /// <summary>A content hash of the source that shapes an index, paths relative to <paramref name="repoRoot"/>.</summary>
    public static string SourceHash(string repoRoot)
    {
        List<string> files = [];
        string src = Path.Combine(repoRoot, "src");
        if (Directory.Exists(src))
            foreach (string project in Directory.EnumerateDirectories(src))
                if (!SearchOnlyProjects.Contains(Path.GetFileName(project)))
                    files.AddRange(SourceFiles(project));
        string systems = Path.Combine(repoRoot, "tests", "Connapse.Eval", "Systems");
        if (Directory.Exists(systems))
            files.AddRange(SourceFiles(systems));
        files.AddRange(RootBuildFiles.Select(f => Path.Combine(repoRoot, f)).Where(File.Exists));

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

    private static IEnumerable<string> SourceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f =>
            {
                string relative = Path.GetRelativePath(directory, f).Replace('\\', '/');
                return !relative.StartsWith("bin/", StringComparison.Ordinal)
                    && !relative.StartsWith("obj/", StringComparison.Ordinal)
                    && !relative.Contains("/bin/", StringComparison.Ordinal)
                    && !relative.Contains("/obj/", StringComparison.Ordinal);
            });
}
