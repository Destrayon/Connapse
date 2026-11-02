namespace Connapse.Architecture.Tests;

/// <summary>Finds the repository from the test's output folder, so baselines and sources are read in place.</summary>
internal static class RepoPaths
{
    private static readonly Lazy<string> RootPath = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Connapse.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            $"No Connapse.slnx above {AppContext.BaseDirectory}");
    });

    public static string Root => RootPath.Value;

    public static string WebProject => Path.Combine(Root, "src", "Connapse.Web");

    public static string Baseline(string fileName) =>
        Path.Combine(Root, "tests", "Connapse.Architecture.Tests", "Baselines", fileName);

    /// <summary>Every C# source file in the web project, minus build output, as (relative path, text).</summary>
    public static IEnumerable<(string Path, string Source)> WebCSharpFiles() =>
        Directory.EnumerateFiles(WebProject, "*.cs", SearchOption.AllDirectories)
            .Select(f => (Full: f, Rel: RelativeToWeb(f)))
            .Where(f => !f.Rel.StartsWith("bin/", StringComparison.Ordinal) && !f.Rel.StartsWith("obj/", StringComparison.Ordinal))
            .OrderBy(f => f.Rel, StringComparer.Ordinal)
            .Select(f => (f.Rel, File.ReadAllText(f.Full)));

    /// <summary>A path relative to the web project, with forward slashes, for stable list entries.</summary>
    public static string RelativeToWeb(string fullPath) =>
        Path.GetRelativePath(WebProject, fullPath).Replace('\\', '/');
}
