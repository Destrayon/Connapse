using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Eval.Runs;
using Connapse.Eval.Runs.MemorySearch;
using FluentAssertions;

namespace Connapse.Eval.Tests.Runs;

[Trait("Category", "Unit")]
public class TablesArmsEvalTests
{
    private static TableQuestion Question(string id, string family = "lookup", string user = "admin") => TableQuestion.Parse(
        $$"""{"id":"{{id}}","split":"test","family":"{{family}}","template":"t","question":"q","user":"{{user}}","answer_type":"number","answer":1,"gold_sql":"select 1","tables":["products"]}""");

    private static ToolTrace Call(string name) => new(name, new Dictionary<string, JsonElement>(), "out");

    private static ArmRun Run(TableQuestion q, string arm, int run, bool correct, int calls = 2, double cost = 0.1,
        AgentOutcome outcome = AgentOutcome.Answered, params string[] tools)
    {
        List<ToolTrace> traces = [.. tools.Select(Call)];
        while (traces.Count < calls)
            traces.Add(Call("table_read"));
        AgentRun agent = new(outcome, "1", [], traces, new TokenUsage(10, 5, 0, 0, cost), 3, null,
            outcome == AgentOutcome.Answered ? JsonSerializer.SerializeToElement(correct ? 1 : 2) : null);
        return new ArmRun(q, arm, run, "q", agent, new TableGrade(correct, false), TimeSpan.FromSeconds(10 + run));
    }

    /// <summary>A second arm, for the multi-arm paths; only the structured arm is registered.</summary>
    private static readonly TableArm Other = new("other", ["table_list", "search_knowledge"]);

    private static TablesArmsEval.Options Options(IReadOnlyList<TableArm>? arms = null, int runs = 3, string split = "test") =>
        new(new TablesRagEval.Options("hybrid", split, "claude-sonnet-5-5", "medium", null, [], true, 1, null), arms ?? TableArm.All, runs);

    [Fact]
    public void Parse_NoNames_GivesEveryArm()
    {
        TableArm.Parse([]).Should().Equal(TableArm.Structured);
    }

    [Fact]
    public void Parse_NamedTwice_KeepsOneInTheOrderGiven()
    {
        TableArm.Parse(["structured", "structured"]).Should().Equal(TableArm.Structured);
    }

    [Fact]
    public void Parse_UnknownName_Throws()
    {
        Action act = () => TableArm.Parse(["both"]);

        act.Should().Throw<ArgumentException>().WithMessage("*'both'*structured*");
    }

    [Theory]
    [InlineData(TablesArmsEval.TablesV1Set, 180)]
    [InlineData(TablesArmsEval.FlexSet, 70)]
    public void RegisteredTestOf_KnownSet_GivesItsTestQuestions(string set, int expected)
    {
        TablesArmsEval.RegisteredTestOf(set).Should().Be(expected);
    }

    [Fact]
    public void RegisteredTestOf_UnknownSet_Throws()
    {
        Action act = () => TablesArmsEval.RegisteredTestOf("flexible");

        act.Should().Throw<ArgumentException>().WithMessage("*'flexible'*tables-v1, flex*");
    }

    [Fact]
    public void Nonconforming_FlexSet_ExpectsSeventyQuestions()
    {
        TablesArmsEval.Options flex = Options(runs: 1) with { Set = TablesArmsEval.FlexSet };

        TablesArmsEval.Nonconforming(flex, TablesArmsEval.RegisteredFlexTest).Should().BeEmpty();
        TablesArmsEval.Nonconforming(flex, TablesRagEval.RegisteredTest).Should().Equal("180 questions, not 70");
    }

    [Fact]
    public void Parse_QuestionWithoutUser_IsAskedAsAdmin()
    {
        TableQuestion q = TableQuestion.Parse(
            """{"id":"flex-test-001","split":"test","family":"median","template":"t","question":"q","answer_type":"number","answer":1,"files":["sales.csv"]}""");

        q.User.Should().Be("admin");
        q.Restricted.Should().BeFalse();
    }

    [Fact]
    public void Structured_Tools_AreTheStructuredTableToolsAndSearch()
    {
        TableArm.Structured.Tools.Should().Equal("table_list", "table_describe", "table_read", "table_aggregate", "search_knowledge", "get_document");
    }

    [Fact]
    public void Missing_StructuredArm_HasEveryTool()
    {
        TableArm.Structured.Missing().Should().BeEmpty();
    }

    [Fact]
    public void Missing_ToolTheServerLacks_IsNamed()
    {
        new TableArm("x", ["no_such_tool", "search_knowledge"]).Missing().Should().Equal("no_such_tool");
    }

    [Fact]
    public void Schedule_TwoArms_InterleavesByQuestionAndSwapsWhoGoesFirst()
    {
        TableQuestion a = Question("a");
        TableQuestion b = Question("b");

        var schedule = TablesArmsEval.Schedule([a, b], [TableArm.Structured, Other], 2);

        schedule.Select(s => $"{s.Question.Id}{s.Run}{s.Arm.Name[..2]}").Should().Equal(
            "a0st", "a0ot", "a1ot", "a1st", "b0st", "b0ot", "b1ot", "b1st");
    }

    [Fact]
    public void Schedule_OneArm_RunsEachQuestionRunsTimes()
    {
        TablesArmsEval.Schedule([Question("a")], [TableArm.Structured], 3).Should().HaveCount(3).And.OnlyContain(s => s.Arm == TableArm.Structured);
    }

    [Fact]
    public void ArmRun_Metrics_CountCallsSearchesAndConfidentWrong()
    {
        ArmRun run = Run(Question("a"), "other", 0, correct: false, calls: 4, tools: ["search_knowledge", "get_document", "table_aggregate"]);

        run.ToolCalls.Should().Be(4);
        run.SearchCalls.Should().Be(2);
        run.ConfidentWrong.Should().BeTrue();
    }

    [Theory]
    [InlineData(AgentOutcome.OutOfCalls)]
    [InlineData(AgentOutcome.NoAnswer)]
    public void ArmRun_NoAnswer_IsNotConfidentWrong(AgentOutcome outcome)
    {
        Run(Question("a"), "other", 0, correct: false, outcome: outcome).ConfidentWrong.Should().BeFalse();
    }

    [Fact]
    public void ArmRun_Right_IsNotConfidentWrong()
    {
        Run(Question("a"), "other", 0, correct: true).ConfidentWrong.Should().BeFalse();
    }

    [Theory]
    [InlineData(new[] { true, true, false }, true)]
    [InlineData(new[] { true, false, false }, false)]
    [InlineData(new[] { true, false }, false)]
    [InlineData(new[] { true, true, true }, true)]
    public void QuestionArm_Correct_NeedsMostRuns(bool[] right, bool expected)
    {
        TableQuestion q = Question("a");
        TablesArmsEval.QuestionArm arm = new(q, "other", [.. right.Select((r, i) => Run(q, "other", i, r))]);

        arm.Correct.Should().Be(expected);
    }

    [Fact]
    public void QuestionArm_CallsWhenRight_AveragesOnlyTheRightRuns()
    {
        TableQuestion q = Question("a");
        TablesArmsEval.QuestionArm arm = new(q, "other", [Run(q, "other", 0, true, calls: 2), Run(q, "other", 1, true, calls: 5), Run(q, "other", 2, false, calls: 20)]);

        arm.CallsWhenRight.Should().Be(3.5);
    }

    [Fact]
    public void QuestionArm_NeverRight_HasNoCallsWhenRight()
    {
        TableQuestion q = Question("a");

        new TablesArmsEval.QuestionArm(q, "other", [Run(q, "other", 0, false)]).CallsWhenRight.Should().BeNull();
    }

    [Fact]
    public void ByQuestion_GroupsOneArmsRunsInRunOrder()
    {
        TableQuestion q = Question("a");
        List<ArmRun> runs = [Run(q, "other", 1, true), Run(q, "structured", 0, false), Run(q, "other", 0, false)];

        List<TablesArmsEval.QuestionArm> other = TablesArmsEval.ByQuestion(runs, "other");

        other.Should().ContainSingle().Which.Runs.Select(r => r.Run).Should().Equal(0, 1);
    }

    [Fact]
    public void Spread_MatchesNumpyLinearPercentiles()
    {
        // np.percentile([1, 2, 3, 4, 10], [50, 90]) = [3.0, 7.6]
        JsonObject spread = TablesArmsEval.Spread([10, 1, 3, 2, 4]);

        spread["mean"]!.GetValue<double>().Should().Be(4);
        spread["median"]!.GetValue<double>().Should().Be(3);
        spread["p90"]!.GetValue<double>().Should().BeApproximately(7.6, 1e-9);
    }

    [Fact]
    public void Spread_Empty_IsZero()
    {
        TablesArmsEval.Spread([])["median"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public void Report_ArmTotals_CountRunsAndMajorities()
    {
        TableQuestion a = Question("a", user: "restricted");
        TableQuestion b = Question("b");
        List<ArmRun> runs =
        [
            Run(a, "other", 0, true, calls: 1, tools: ["search_knowledge"]), Run(a, "other", 1, true, calls: 3), Run(a, "other", 2, false, calls: 5),
            Run(b, "other", 0, false, outcome: AgentOutcome.OutOfCalls), Run(b, "other", 1, false), Run(b, "other", 2, true),
        ];

        JsonNode other = TablesArmsEval.Report(Options([Other]), 2, runs)["arms"]!["other"]!;

        other["all"]!["correct"]!.GetValue<int>().Should().Be(1);
        other["restricted"]!["accuracy"]!.GetValue<double>().Should().Be(1);
        other["admin"]!["accuracy"]!.GetValue<double>().Should().Be(0);
        other["runs"]!.GetValue<int>().Should().Be(6);
        other["runAccuracy"]!.GetValue<double>().Should().Be(0.5);
        other["confidentWrongRuns"]!.GetValue<int>().Should().Be(2);
        other["searchCalls"]!["mean"]!.GetValue<double>().Should().BeApproximately(1.0 / 6, 0.01);
        other["outcomes"]!["OutOfCalls"]!.GetValue<int>().Should().Be(1);
        other["costPerQuestionUsd"]!.GetValue<double>().Should().Be(0.1);
        other["wallSeconds"]!["median"]!.GetValue<double>().Should().Be(11);
    }

    [Fact]
    public void Report_UnregisteredArm_IsDiagnostic()
    {
        JsonObject report = TablesArmsEval.Report(Options([Other], runs: 2, split: "tune"), 1, [Run(Question("a"), "other", 0, true)]);

        report["conforming"]!.GetValue<bool>().Should().BeFalse();
        report["diagnosticOnly"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain(
            ["split is tune, not test", "2 runs per arm, not 1", "only arm other ran"]);
    }

    [Fact]
    public void Nonconforming_RegisteredRun_IsEmpty()
    {
        TablesArmsEval.Nonconforming(Options(runs: 1), TablesRagEval.RegisteredTest).Should().BeEmpty();
    }

    [Fact]
    public void RunLines_AddTheArmFields()
    {
        string line = TablesArmsEval.RunLines([Run(Question("a"), "other", 1, false, calls: 2, tools: ["get_document"])]).Single();

        JsonNode node = JsonNode.Parse(line)!;
        node["id"]!.GetValue<string>().Should().Be("a");
        node["arm"]!.GetValue<string>().Should().Be("other");
        node["run"]!.GetValue<int>().Should().Be(2);
        node["toolCalls"]!.GetValue<int>().Should().Be(2);
        node["searchCalls"]!.GetValue<int>().Should().Be(1);
        node["confidentWrong"]!.GetValue<bool>().Should().BeTrue();
        node["wallSeconds"]!.GetValue<double>().Should().Be(11);
        node["tokens"]!["input"]!.GetValue<long>().Should().Be(10);
    }

    [Fact]
    public void ReadRun_RunLinesReadBack_ScoreTheSameReport()
    {
        TableQuestion a = Question("a", family: "lookup", user: "restricted");
        TableQuestion b = Question("b", family: "aggregate");
        List<ArmRun> runs =
        [
            Run(a, "structured", 0, true, calls: 3, tools: ["search_knowledge"]), Run(a, "other", 0, true, calls: 1, cost: 0.2),
            Run(a, "other", 1, false, calls: 2), Run(a, "structured", 1, true, calls: 4),
            Run(b, "structured", 0, false, outcome: AgentOutcome.OutOfCalls), Run(b, "other", 0, true, calls: 2),
            Run(b, "other", 1, true, calls: 3, tools: ["get_document"]), Run(b, "structured", 1, false, calls: 6),
        ];

        List<ArmRun> readBack = [.. TablesArmsEval.RunLines(runs).Select(TablesArmsEval.ReadRun)];

        readBack.Select(r => (r.Question.Id, r.Arm, r.Run, r.ToolCalls, r.SearchCalls, r.ConfidentWrong, r.Question.Restricted))
            .Should().Equal(runs.Select(r => (r.Question.Id, r.Arm, r.Run, r.ToolCalls, r.SearchCalls, r.ConfidentWrong, r.Question.Restricted)));
        TablesArmsEval.Report(Options(runs: 2), 2, readBack).ToJsonString()
            .Should().Be(TablesArmsEval.Report(Options(runs: 2), 2, runs).ToJsonString());
    }

    [Fact]
    public void SavedRuns_JournalLines_KeyedByQuestionArmAndRunWithTheLastLineWinning()
    {
        List<string> lines =
        [
            .. TablesArmsEval.RunLines([Run(Question("a"), "other", 0, false), Run(Question("a"), "structured", 0, true)]),
            "",
            .. TablesArmsEval.RunLines([Run(Question("a"), "other", 0, true)]),
        ];

        Dictionary<(string Id, string Arm, int Run), string> saved = TablesArmsEval.SavedRuns(lines);

        saved.Keys.Should().BeEquivalentTo([("a", "other", 0), ("a", "structured", 0)]);
        TablesArmsEval.ReadRun(saved[("a", "other", 0)]).Grade.Correct.Should().BeTrue();
    }

    [Fact]
    public async Task RunJournal_Keep_LeavesSavedLinesInPlace()
    {
        string path = Path.Combine(Path.GetTempPath(), $"journal-{Guid.NewGuid():N}.runs.jsonl");
        await File.WriteAllTextAsync(path, "saved\n");
        try
        {
            RunJournal journal = new(path, keep: true);
            await journal.AppendAsync(Run(Question("b"), "other", 0, true), CancellationToken.None);

            string[] lines = await File.ReadAllLinesAsync(path);
            lines.Should().HaveCount(2);
            lines[0].Should().Be("saved");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ScoreAsync_RunsFile_WritesTheReport()
    {
        string runsPath = Path.Combine(Path.GetTempPath(), $"score-{Guid.NewGuid():N}.runs.jsonl");
        string outPath = Path.ChangeExtension(runsPath, ".json");
        await File.WriteAllLinesAsync(runsPath, TablesArmsEval.RunLines([Run(Question("a"), "other", 0, true), Run(Question("a"), "structured", 0, false)]));
        try
        {
            TablesArmsEval.Options options = Options(runs: 1) with { Base = Options().Base with { OutPath = outPath } };

            int code = await TablesArmsEval.ScoreAsync(options, runsPath, TextWriter.Null, CancellationToken.None);

            code.Should().Be(0);
            JsonNode report = JsonNode.Parse(await File.ReadAllTextAsync(outPath))!;
            report["questions"]!.GetValue<int>().Should().Be(1);
            report["arms"]!["structured"]!["all"]!["correct"]!.GetValue<int>().Should().Be(0);
            report["scoredFrom"]!.GetValue<string>().Should().Be(Path.GetFileName(runsPath));
        }
        finally
        {
            File.Delete(runsPath);
            File.Delete(outPath);
        }
    }

    [Fact]
    public async Task RunJournal_AppendAsync_WritesEachRunAsItFinishes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"runjournal-{Guid.NewGuid():N}.runs.jsonl");
        await File.WriteAllTextAsync(path, "stale line\n");
        try
        {
            RunJournal journal = new(path);
            File.ReadAllLines(path).Should().BeEmpty();

            await journal.AppendAsync(Run(Question("a"), "other", 0, true), CancellationToken.None);
            File.ReadAllLines(path).Should().ContainSingle();

            await journal.AppendAsync(Run(Question("b"), "structured", 1, false), CancellationToken.None);
            string[] lines = File.ReadAllLines(path);
            lines.Select(l => JsonNode.Parse(l)!["id"]!.GetValue<string>()).Should().Equal("a", "b");
            JsonNode.Parse(lines[1])!["arm"]!.GetValue<string>().Should().Be("structured");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunJournal_NoPath_WritesNothing()
    {
        RunJournal journal = new(null);

        Func<Task> append = () => journal.AppendAsync(Run(Question("a"), "other", 0, true), CancellationToken.None);

        await append.Should().NotThrowAsync();
    }
}
