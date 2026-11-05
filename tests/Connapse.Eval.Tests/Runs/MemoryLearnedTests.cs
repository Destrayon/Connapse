using System.Text.Json;
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

    private static Dictionary<string, JsonElement> Input(object o) =>
        JsonSerializer.SerializeToElement(o).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static AgentRun Run(params ToolTrace[] calls) =>
        new(AgentOutcome.Answered, "a", [], calls, new TokenUsage(100, 10, 50, 0, 0.002), 2, null);

    [Fact]
    public void Dataset_TestSplit_HasTheRegisteredSessionTwoSets()
    {
        List<MemoryLearnedEval.LearnedTask> tasks = MemoryLearnedEval.Select(MemoryLearnedEval.ReadTasks(Path.Combine(Root, "tasks.jsonl")), "test", null);
        List<MemoryLearnedEval.LearnedTask> second = [.. tasks.Where(t => t.Session == 2)];

        second.Should().HaveCount(MemoryLearnedEval.RegisteredTasks);
        second.Count(t => t.Task.InMemorySet).Should().Be(64);
        tasks.Count(t => t.Session == 1).Should().Be(76);
        tasks.Where(t => t.Session == 1).Select(t => t.Task.Kind).Should().NotContain("control");
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
    public void Select_LimitCountsSessionTwoFamilies_AndKeepsTheirSessionOneQuestions()
    {
        List<MemoryLearnedEval.LearnedTask> tasks =
        [
            Learned("a", 1), Learned("b", 1), Learned("a", 2), Learned("c", 2, "control"), Learned("b", 2), Learned("d", 2, split: "dev"),
        ];

        MemoryLearnedEval.Select(tasks, "test", 2).Select(t => t.Task.Id).Should().Equal("a-s1", "a-s2", "c-s2");
        MemoryLearnedEval.Select(tasks, "test", null).Should().HaveCount(5);
    }

    [Fact]
    public void Writes_CountsRememberAndCorrect_AndWhetherEachCitedSources()
    {
        AgentRun run = Run(
            new ToolTrace("search_knowledge", Input(new { query = "x" }), "[]"),
            new ToolTrace("memory_remember", Input(new { subject = "s", sources = new[] { "doc" } }), "ok"),
            new ToolTrace("memory_remember", Input(new { subject = "s", sources = Array.Empty<string>() }), "ok"),
            new ToolTrace("memory_correct", Input(new { factId = "f" }), "ok"),
            new ToolTrace("memory_recall", Input(new { query = "x" }), "[]"));

        MemoryLearnedEval.Writes(run).Should().Equal(true, false, false);
    }

    [Fact]
    public void Session1_SummarisesScoresAndWrites()
    {
        AgentRun wrote = Run(new ToolTrace("memory_remember", Input(new { sources = new[] { "doc" } }), "ok"));
        RunScore one = new(1, false, true, true, 1, 1, 2, 0, 100, 10, 50);
        RunScore zero = one with { Score = 0 };

        var summary = MemoryLearnedEval.Session1([(Learned("a", 1), (wrote, one)), (Learned("b", 1), (Run(), zero))]);

        ((int)summary["runs"]!).Should().Be(2);
        ((double)summary["meanScore"]!).Should().Be(0.5);
        ((int)summary["memoryWrites"]!).Should().Be(1);
        ((int)summary["writesCitingSources"]!).Should().Be(1);
        ((int)summary["runsThatWrote"]!).Should().Be(1);
    }

    [Fact]
    public void Report_SessionTwoResultsAtTheRegisteredSize_Conform()
    {
        MemorySearchEval.Options options = new("hybrid", "test", "claude-sonnet-5-5", "medium", 3, null, 1, null);
        List<MemorySearchEval.TaskResult> results =
            [.. Enumerable.Range(0, MemoryLearnedEval.RegisteredTasks).Select(i => new MemorySearchEval.TaskResult(Learned($"f{i}", 2).Task, [], []))];

        MemorySearchEval.Nonconforming(options, results, MemoryLearnedEval.RegisteredTasks).Should().Equal("104 tasks are missing runs");
        MemorySearchEval.Nonconforming(options, results).Should().Contain("104 tasks, not 112");
    }

    [Fact]
    public void Prompts_LearnPromptAsksToSaveMemory_WhileSessionTwoPromptNeverMentionsIt()
    {
        Guid container = Guid.NewGuid();

        MemoryLearnedEval.LearnPrompt(container).Should().Contain("memory_remember").And.Contain(container.ToString());
        MemorySearchEval.SystemPrompt(container).Should().NotContainEquivalentOf("memory");
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "eval", "MANIFEST.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }
}
