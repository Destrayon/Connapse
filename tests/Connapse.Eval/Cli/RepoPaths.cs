namespace Connapse.Eval.Cli;

public sealed record RepoPaths(string RepoRoot)
{
    public string EvalRoot => Path.Combine(RepoRoot, "eval");
    public string ManifestPath => Path.Combine(EvalRoot, "MANIFEST.json");
    public string CacheRoot => Path.Combine(EvalRoot, ".cache");
    public string RunsRoot => Path.Combine(EvalRoot, "runs");
    public string WebContentRoot => Path.Combine(RepoRoot, "src", "Connapse.Web");

    public static RepoPaths Find(string start)
    {
        DirectoryInfo? dir = new(start);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Connapse.slnx")))
            dir = dir.Parent;
        return dir is null
            ? throw new InvalidOperationException($"Could not find Connapse.slnx above {start}. Run from inside the repository.")
            : new RepoPaths(dir.FullName);
    }
}
