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

    /// <summary>A path relative to the web project, with forward slashes, for stable list entries.</summary>
    public static string RelativeToWeb(string fullPath) =>
        Path.GetRelativePath(WebProject, fullPath).Replace('\\', '/');
}
