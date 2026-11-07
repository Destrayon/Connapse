using Connapse.Eval.Runs;
using Connapse.Eval.Runs.MemorySearch;
using FluentAssertions;

namespace Connapse.Eval.Tests.Runs;

[Trait("Category", "Unit")]
public class MemoryLearnedTests
{
    private static readonly string Root = Path.Combine(RepoRoot(), "eval", "datasets", "memory-learned");

    private static MemoryLearnedEval.LearnedTask Learned(string family, int session, string kind = "current", string split = "test") =>
        new(new SearchTask($"{family}-s{session}", kind, split, true, "q", ["ml-0001"], [], "a", null), family, session);

    [Fact]
    public void Dataset_TestSplit_HasTheRegisteredSessionTwoSets()
    {
        List<MemoryLearnedEval.LearnedTask> second = MemoryLearnedEval.Select(MemoryLearnedEval.ReadTasks(Path.Combine(Root, "tasks.jsonl")), "test", null);

        second.Should().HaveCount(MemoryLearnedEval.RegisteredTasks);
        second.Should().OnlyContain(t => t.Session == 2);
        second.Count(t => t.Task.InMemorySet).Should().Be(64);
    }

    [Fact]
    public void Dataset_EveryCitationResolves_AndSessionOneNeverCitesALateDocument()
    {
        List<MemoryLearnedEval.LearnedTask> tasks = MemoryLearnedEval.ReadTasks(Path.Combine(Root, "tasks.jsonl"));
        HashSet<string> early = [.. MemorySearchEval.ReadCorpus(Path.Combine(Root, "corpus.jsonl")).Select(d => d.Id)];
        HashSet<string> late = [.. MemorySearchEval.ReadCorpus(Path.Combine(Root, "late.jsonl")).Select(d => d.Id)];

        early.Should().NotIntersectWith(late);
        tasks.SelectMany(t => t.Task.Gold.Concat(t.Task.Stale)).Should().OnlyContain(id => early.Contains(id) || late.Contains(id));
        tasks.Where(t => t.Session == 1).SelectMany(t => t.Task.Gold).Should().OnlyContain(id => early.Contains(id));
        tasks.Where(t => t.Task.Kind == "stale" && t.Session == 2).Should().OnlyContain(t => t.Task.Gold.All(late.Contains));
    }

    [Fact]
    public void Select_LimitCountsSessionTwoFamilies_AndDropsSessionOne()
    {
        List<MemoryLearnedEval.LearnedTask> tasks =
        [
            Learned("a", 1), Learned("b", 1), Learned("a", 2), Learned("c", 2, "control"), Learned("b", 2), Learned("d", 2, split: "dev"),
        ];

        MemoryLearnedEval.Select(tasks, "test", 2).Select(t => t.Task.Id).Should().Equal("a-s2", "c-s2");
        MemoryLearnedEval.Select(tasks, "test", null).Should().HaveCount(3);
    }

    [Fact]
    public void Report_SessionTwoResultsAtTheRegisteredSize_Conform()
    {
        MemorySearchEval.Options options = new("hybrid", "test", "claude-sonnet-5-5", "medium", 3, null, 1, null);
        List<MemorySearchEval.TaskResult> results =
            [.. Enumerable.Range(0, MemoryLearnedEval.RegisteredTasks).Select(i => new MemorySearchEval.TaskResult(Learned($"f{i}", 2).Task, []))];

        MemorySearchEval.Nonconforming(options, results, MemoryLearnedEval.RegisteredTasks).Should().Equal("104 tasks are missing runs");
        MemorySearchEval.Nonconforming(options, results).Should().Contain("104 tasks, not 112");
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "eval", "MANIFEST.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }
}
