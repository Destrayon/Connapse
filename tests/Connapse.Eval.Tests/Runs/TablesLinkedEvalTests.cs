using System.Text.Json;
using Connapse.Application.Search;
using Connapse.Application.Tables;
using Connapse.Eval.Runs;
using Connapse.Eval.Runs.MemorySearch;
using FluentAssertions;

namespace Connapse.Eval.Tests.Runs;

// CON-312: the passage-link eval counts a followed link exactly as the bar defines it.
[Trait("Category", "Unit")]
public class TablesLinkedEvalTests
{
    private const string Table = "3f2c9a10-4b7e-4c1a-9d2e-8a5b6c7d8e9f";
    private const string Other = "11111111-2222-4333-8444-555555555555";

    private static ToolTrace Call(string name, string output, string? table = null, bool asString = false)
    {
        Dictionary<string, JsonElement> input = new(StringComparer.Ordinal);
        if (table is not null)
        {
            string request = $$"""{"table": "{{table}}", "aggregates": [{"aggregate": "count"}]}""";
            input["request"] = asString ? JsonSerializer.SerializeToElement(request) : JsonDocument.Parse(request).RootElement.Clone();
        }
        return new ToolTrace(name, input, output);
    }

    private static ToolTrace Search(string id = Table) =>
        Call("search_knowledge", $"Content:\n[Table \"fleet, table 1\" (id {id}), columns: depot. Its rows are in a table.]");

    [Fact]
    public void FollowedLink_ReadOfATableIdFromSearch_Counts()
    {
        TablesLinkedEval.FollowedAt([Search(), Call("table_aggregate", "{\"rows\":[[3]]}", Table)]).Should().Be(1);
    }

    [Fact]
    public void FollowedLink_RequestSentAsAJsonString_Counts()
    {
        TablesLinkedEval.FollowedLink([Search(), Call("table_read", "{\"rows\":[]}", Table, asString: true)]).Should().BeTrue();
    }

    [Fact]
    public void FollowedLink_IdFromGetDocumentTablesLine_Counts()
    {
        ToolTrace document = Call("get_document", $"Document: fleet.docx\nTables: \"fleet, table 1\" (id {Table})\n---\ntext");

        TablesLinkedEval.FollowedLink([document, Call("table_read", "{}", Table)]).Should().BeTrue();
    }

    [Fact]
    public void FollowedLink_TableListBeforeTheRead_DoesNotCount()
    {
        TablesLinkedEval.FollowedLink([Search(), Call("table_list", $"[{{\"id\":\"{Table}\"}}]"), Call("table_read", "{}", Table)])
            .Should().BeFalse("the bar counts a read with no table_list before it");
    }

    [Fact]
    public void FollowedLink_FailedRead_DoesNotCount()
    {
        TablesLinkedEval.FollowedLink([Search(), Call("table_read", "Error: Unknown column 'x'.", Table)]).Should().BeFalse();
    }

    [Fact]
    public void FollowedLink_FailedThenSuccessfulRead_CountsTheSuccess()
    {
        TablesLinkedEval.FollowedAt([Search(), Call("table_read", "Error: bad", Table), Call("table_read", "{}", Table)]).Should().Be(2);
    }

    [Fact]
    public void FollowedLink_IdSeenFirstOutsideSearch_DoesNotCount()
    {
        ToolTrace describe = Call("table_describe", $"{{\"id\":\"{Table}\"}}");

        TablesLinkedEval.FollowedLink([describe, Search(), Call("table_read", "{}", Table)])
            .Should().BeFalse("the id first appeared in a table tool's answer, not a passage");
    }

    [Fact]
    public void FollowedLink_ReadOfAnotherTable_DoesNotCount()
    {
        TablesLinkedEval.FollowedLink([Search(), Call("table_read", "{}", Other)]).Should().BeFalse();
    }

    [Fact]
    public void TableCall_AnyTableTool_Counts()
    {
        TablesLinkedEval.TableCall([Search()]).Should().BeFalse();
        TablesLinkedEval.TableCall([Search(), Call("table_list", "[]")]).Should().BeTrue();
    }

    [Fact]
    public void Unlinked_OverridesOnlySearchAndTheDocumentTableList()
    {
        TablesLinkedEval.Unlinked.Keys.Should().BeEquivalentTo([typeof(SearchKnowledge), typeof(GetFileTables)]);
    }

    [Fact]
    public void Reports_AreTheTenLinkedDocumentsUnderTheirOwnNames()
    {
        string root = Path.Combine(RepoRoot(), "eval", "datasets", "tables", "linked");

        var reports = TablesLinkedEval.Reports(root);

        reports.Should().HaveCount(10);
        reports.Should().OnlyContain(d => d.FileName == Path.GetFileName(d.FilePath) && d.Id == "linked/docs/" + d.FileName);
        LinkedQuestion.ReadAll(Path.Combine(root, "questions.jsonl")).Select(q => q.Document)
            .Should().OnlyContain(doc => reports.Any(r => r.Id == doc), "every question's document is uploaded");
    }

    [Fact]
    public void Report_CountsTheBarFromBothSetups()
    {
        List<TablesLinkedEval.Result> results = [];
        for (int i = 0; i < 20; i++)
        {
            LinkedQuestion q = Question($"t{i}", "table");
            results.Add(Result(q, TablesLinkedEval.LinkedSetup, correct: i < 18, followed: i < 17, tableCall: true, cost: 0.11));
            results.Add(Result(q, TablesLinkedEval.UnlinkedSetup, correct: i < 12, followed: false, tableCall: true, cost: 0.10));
        }
        for (int i = 0; i < 10; i++)
        {
            LinkedQuestion q = Question($"p{i}", "prose");
            results.Add(Result(q, TablesLinkedEval.LinkedSetup, correct: true, followed: false, tableCall: i < 1, cost: 0.11));
            results.Add(Result(q, TablesLinkedEval.UnlinkedSetup, correct: true, followed: false, tableCall: false, cost: 0.10));
        }
        TablesLinkedEval.Options options = new("hybrid", "claude-sonnet-5-5", "medium", null, [], 1, null);

        var bar = TablesLinkedEval.Report(options, results)["bar"]!;

        bar["1_followedLink"]!["followed"]!.GetValue<int>().Should().Be(17);
        bar["1_followedLink"]!["pass"]!.GetValue<bool>().Should().BeTrue("17 of 20 is 85%");
        bar["2_helps"]!["tableGain"]!.GetValue<int>().Should().Be(6);
        bar["2_helps"]!["pass"]!.GetValue<bool>().Should().BeTrue();
        bar["3_noDistraction"]!["proseWithTableCall"]!.GetValue<int>().Should().Be(1);
        bar["3_noDistraction"]!["pass"]!.GetValue<bool>().Should().BeTrue();
        bar["5_cost"]!["ratio"]!.GetValue<double>().Should().Be(1.1);
        bar["5_cost"]!["pass"]!.GetValue<bool>().Should().BeTrue();
        TablesLinkedEval.Report(options, results)["conforming"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Report_FifteenOfTwentyFollowed_FailsTheFirstBar()
    {
        List<TablesLinkedEval.Result> results = [.. Enumerable.Range(0, 20).Select(i =>
            Result(Question($"t{i}", "table"), TablesLinkedEval.LinkedSetup, true, followed: i < 15, true, 0.1))];

        var bar = TablesLinkedEval.Report(new("hybrid", "claude-sonnet-5-5", "medium", null, [], 1, null), results)["bar"]!;

        bar["1_followedLink"]!["pass"]!.GetValue<bool>().Should().BeFalse("15 of 20 is 75%, under 80%");
    }

    private static LinkedQuestion Question(string id, string kind) =>
        new(id, kind, "q", "number", JsonSerializer.SerializeToElement(1), "linked/docs/a.docx");

    private static TablesLinkedEval.Result Result(LinkedQuestion q, string setup, bool correct, bool followed, bool tableCall, double cost) =>
        new(q, setup, new AgentRun(AgentOutcome.Answered, "1", [], [], new TokenUsage(0, 0, 0, 0, cost), 3, null), correct, followed, tableCall);

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "eval", "MANIFEST.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("No repo root above the test binaries.");
    }
}
