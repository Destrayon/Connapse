using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Core;
using Connapse.Eval.Runs;
using Connapse.Eval.Runs.MemorySearch;
using Connapse.Storage.Tables;
using FluentAssertions;

namespace Connapse.Eval.Tests.Runs;

[Trait("Category", "Unit")]
public class TablesSavedEvalTests
{
    private static TableQuestion Question(string id, string template, string split = "test", string user = "admin") => TableQuestion.Parse(
        $$"""{"id":"{{id}}","split":"{{split}}","family":"lookup","template":"{{template}}","question":"q","user":"{{user}}","answer_type":"number","answer":1,"gold_sql":"select 1","tables":["orders"]}""");

    private static ToolTrace Call(string name, string output) => new(name, new Dictionary<string, JsonElement>(), output);

    private static Dictionary<string, Guid> FakeIds() =>
        TablesSavedLibrary.Tables.Concat(["accounts"]).ToDictionary(t => t, _ => Guid.NewGuid());

    [Fact]
    public void Library_EveryQueryResolved_PassesTheSaveCheck()
    {
        Dictionary<string, Guid> ids = FakeIds();

        foreach (LibraryQuery query in TablesSavedLibrary.Queries)
        {
            Action save = () => SavedQueryBinder.Validate(TablesSavedLibrary.Resolve(query.Definition, ids));
            save.Should().NotThrow(query.Definition.Name);
        }
    }

    [Fact]
    public void Library_Names_AreUnique()
    {
        TablesSavedLibrary.Queries.Select(q => q.Definition.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void ProbeQuery_Resolved_PassesTheSaveCheckAndFillsTheOwnerFromLogin()
    {
        SavedQueryDefinition resolved = TablesSavedLibrary.Resolve(TablesSavedEval.ProbeDefinition, FakeIds());

        Action save = () => SavedQueryBinder.Validate(resolved);

        save.Should().NotThrow();
        resolved.Parameters.Should().ContainSingle().Which.FromLogin.Should().Be(SavedQueryLoginField.UserId);
    }

    [Fact]
    public void Resolve_UnknownTable_Throws()
    {
        Action act = () => TablesSavedLibrary.Resolve(TablesSavedLibrary.Queries[0].Definition, new Dictionary<string, Guid>());

        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Covered_MixedQuestions_KeepsOnlyAdminTestQuestionsWithALibraryTemplate()
    {
        string covered = TablesSavedLibrary.Templates.First();
        List<TableQuestion> questions =
        [
            Question("q1", covered),
            Question("q2", covered, split: "tune"),
            Question("q3", covered, user: "restricted"),
            Question("q4", "not-a-library-template"),
        ];

        TablesSavedEval.Covered(questions).Select(q => q.Id).Should().Equal("q1");
    }

    [Fact]
    public void Covered_MoreThanTheRegisteredCount_TakesTheSameThirtyInIdOrder()
    {
        string covered = TablesSavedLibrary.Templates.First();
        List<TableQuestion> questions = [.. Enumerable.Range(0, 50).Select(i => Question($"test-{i:000}", covered))];

        List<string> first = [.. TablesSavedEval.Covered(questions).Select(q => q.Id)];
        List<string> reversed = [.. TablesSavedEval.Covered(Enumerable.Reverse(questions)).Select(q => q.Id)];

        first.Should().HaveCount(TablesSavedEval.RegisteredQuestions).And.BeInAscendingOrder(StringComparer.Ordinal);
        reversed.Should().Equal(first);
    }

    [Fact]
    public void CrossUserRowsIn_OtherUsersInASavedQueryAnswer_CountsEachMention()
    {
        List<ToolTrace> calls =
        [
            Call("table_query_run", $"[{{\"owner\":\"{TablesSavedEval.OtherA}\"}},{{\"owner\":\"{TablesSavedEval.OtherB}\"}}]"),
            Call("table_query_run", $"[{{\"owner\":\"{TablesSavedEval.Caller}\"}}]"),
            Call("table_read", $"[{{\"owner\":\"{TablesSavedEval.OtherA}\"}}]"),
        ];

        TablesSavedEval.CrossUserRowsIn(calls).Should().Be(2);
    }

    [Fact]
    public void ProbeCsv_CallerOwnsExactlyTheExpectedAccounts()
    {
        string[] rows = TablesSavedEval.ProbeCsv().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        rows.Skip(1).Where(r => r.Contains(TablesSavedEval.Caller.ToString(), StringComparison.Ordinal)).Select(r => r.Split(',')[0])
            .Should().Equal("A-1001", "A-1003");
        TablesSavedEval.ProbePrompts()[0].Answer!.Value.GetRawText().Should().Be("""["A-1001","A-1003"]""");
    }

    [Fact]
    public void SavedRuns_ErrorOutput_IsNotCounted()
    {
        List<ToolTrace> calls = [Call("table_query_run", "Error: saved-query-not-found"), Call("table_query_run", "{\"rows\":[]}")];

        TablesSavedEval.SavedRuns(calls).Should().Be(1);
    }

    [Theory]
    [InlineData(20, 23, 1.0, 1.0, 0, true, true)]   // +3 questions
    [InlineData(20, 20, 1.0, 0.85, 0, true, true)]  // same correctness, 15% cheaper
    [InlineData(20, 22, 1.0, 0.9, 0, true, false)]  // neither gain
    [InlineData(20, 19, 1.0, 0.5, 0, true, false)]  // cheaper but worse
    [InlineData(20, 25, 1.0, 0.5, 1, true, false)]  // one cross-user row
    [InlineData(20, 25, 1.0, 0.5, 0, false, false)] // the login query doesn't work
    public void Verdict_EachPartOfTheBar_DecidesThePass(int baseline, int saved, double baselineCost, double savedCost,
        int crossUserRows, bool loginWorks, bool pass)
    {
        JsonObject verdict = TablesSavedEval.Verdict(baseline, saved, baselineCost, savedCost, crossUserRows, loginWorks);

        verdict["pass"]!.GetValue<bool>().Should().Be(pass);
    }

    [Fact]
    public void Nonconforming_RegisteredRun_HasNoReasons()
    {
        TablesSavedEval.Options options = new(new TablesRagEval.Options("hybrid", "test", "claude-sonnet-5-5", "medium", null, [], true, 1, null),
            TablesSavedEval.RegisteredRuns, CheckLibrary: false, SkipProbe: false);

        TablesSavedEval.Nonconforming(options, TablesSavedEval.RegisteredQuestions).Should().BeEmpty();
        TablesSavedEval.Nonconforming(options with { SkipProbe = true }, 10).Should().HaveCount(2);
    }
}
