using System.Text.Json.Nodes;
using Connapse.Eval.Runs;
using Connapse.Eval.Runs.MemorySearch;
using FluentAssertions;

namespace Connapse.Eval.Tests.Runs;

[Trait("Category", "Unit")]
public class MemorySearchComparisonTests
{
    private static string Line(string task, int rep, double score) =>
        new JsonObject { ["task"] = task, ["rep"] = rep, ["outcome"] = "Answered", ["score"] = score }.ToJsonString();

    private static SortedDictionary<string, List<double>> Arm(int tasks, Func<int, int, double> score, int reps = 3) =>
        MemorySearchComparison.TaskScores(
            Enumerable.Range(0, tasks).SelectMany(t => Enumerable.Range(1, reps).Select(r => Line($"t-{t:D3}", r, score(t, r)))));

    [Fact]
    public void TaskScores_GroupsRepetitionsByTask()
    {
        SortedDictionary<string, List<double>> scores = MemorySearchComparison.TaskScores(
            [Line("b", 1, 1), Line("a", 1, 0), "", Line("b", 2, 0)]);

        scores.Keys.Should().Equal("a", "b");
        scores["b"].Should().Equal(1, 0);
    }

    [Fact]
    public void Compare_IdenticalArms_Passes()
    {
        SortedDictionary<string, List<double>> arm = Arm(40, (t, r) => (t + r) % 3 == 0 ? 0 : 1);

        JsonObject result = MemorySearchComparison.Compare(arm, Arm(40, (t, r) => (t + r) % 3 == 0 ? 0 : 1));

        ((string)result["verdict"]!).Should().Be("PASS");
        ((double)result["meanDifference"]!).Should().Be(0);
        ((int)result["tasks"]!).Should().Be(40);
        ((int)result["runs"]!).Should().Be(120);
    }

    [Fact]
    public void Compare_CandidateLosesAFifthOfTasks_Fails()
    {
        JsonObject result = MemorySearchComparison.Compare(Arm(40, (_, _) => 1), Arm(40, (t, _) => t % 5 == 0 ? 0 : 1));

        ((string)result["verdict"]!).Should().Be("FAIL");
        ((int)result["tasksWorse"]!).Should().Be(8);
        ((double)result["ci90"]![0]!).Should().BeLessThan(-0.05);
    }

    [Fact]
    public void Compare_WiderMargin_PassesTheSameLoss()
    {
        JsonObject result = MemorySearchComparison.Compare(Arm(40, (_, _) => 1), Arm(40, (t, _) => t % 5 == 0 ? 0 : 1), margin: 0.5);

        ((string)result["verdict"]!).Should().Be("PASS");
    }

    [Fact]
    public void Compare_DifferentTasks_Throws()
    {
        FluentActions.Invoking(() => MemorySearchComparison.Compare(Arm(10, (_, _) => 1), Arm(11, (_, _) => 1)))
            .Should().Throw<ArgumentException>().WithMessage("*same tasks*");
    }

    [Fact]
    public void Compare_DifferentRepetitions_Throws()
    {
        FluentActions.Invoking(() => MemorySearchComparison.Compare(Arm(10, (_, _) => 1), Arm(10, (_, _) => 1, reps: 2)))
            .Should().Throw<ArgumentException>().WithMessage("*different number of times*");
    }

    [Fact]
    public void ToolSet_NoToolsGiven_IsTheRegisteredSearchTools()
    {
        new MemorySearchEval.Options("hybrid", "test", "m", "e", 3, null, 1, null).ToolSet.Should().Equal(MemorySearchEval.SearchTools);
        new MemorySearchEval.Options("hybrid", "test", "m", "e", 3, null, 1, null, Tools: []).ToolSet.Should().Equal(MemorySearchEval.SearchTools);
    }

    [Fact]
    public void ToolSet_ToolsGiven_IsThoseTools()
    {
        new MemorySearchEval.Options("hybrid", "test", "m", "e", 3, null, 1, null, Tools: ["search_knowledge", "get_documents"])
            .ToolSet.Should().Equal("search_knowledge", "get_documents");
    }
}
