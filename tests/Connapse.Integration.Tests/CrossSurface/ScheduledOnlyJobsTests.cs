using System.Text.RegularExpressions;
using FluentAssertions;

namespace Connapse.Integration.Tests.CrossSurface;

/// <summary>
/// The capture leaves out jobs that only a recurring sweep enqueues. That holds only while the
/// sweep is their one enqueue site, so this reads the source: a second site, which an action
/// could reach, fails here until the job is captured again.
/// </summary>
[Trait("Category", "Unit")]
public partial class ScheduledOnlyJobsTests
{
    [Fact]
    public void ScheduledOnlyJobs_AreEnqueuedOnlyByTheirSweep()
    {
        var sites = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => Enqueue().Matches(File.ReadAllText(file)).Select(m => (File: Path.GetFileName(file), Job: $"{m.Groups["type"].Value}.{m.Groups["method"].Value}")))
            .Where(site => SideEffects.ScheduledOnly.Contains(site.Job))
            .ToList();

        sites.Should().Equal([("SummaryJobs.cs", "ISummaryJobs.RollupContainerAsync")],
            "RollupContainerAsync is enqueued only by SweepStaleContainersAsync");
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Connapse.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Connapse.slnx not found above the test output.");
    }

    [GeneratedRegex(@"(?:Enqueue|Schedule)<(?<type>\w+)>\(\s*\w+\s*=>\s*\w+\.(?<method>\w+)\(")]
    private static partial Regex Enqueue();
}
