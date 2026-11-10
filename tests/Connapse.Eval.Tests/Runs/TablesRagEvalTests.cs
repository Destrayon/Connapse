using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Eval.Runs;
using Connapse.Eval.Runs.MemorySearch;
using FluentAssertions;

namespace Connapse.Eval.Tests.Runs;

[Trait("Category", "Unit")]
public class TablesRagEvalTests
{
    private sealed class FakeTools : IToolbox
    {
        public IReadOnlyList<ToolSpec> Tools { get; } = [new("search_knowledge", "search", new Dictionary<string, JsonElement>(), [])];

        public Task<string> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct) =>
            Task.FromResult("rows");
    }

    private static Dictionary<string, JsonElement> Input(object o) =>
        JsonSerializer.SerializeToElement(o).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static TableQuestion Question(string json) => TableQuestion.Parse(json);

    private const string Lookup =
        """{"id":"test-001","split":"test","family":"lookup","template":"t","question":"What is the price of P-1?","user":"admin","answer_type":"number","answer":12.5,"gold_sql":"select 1","tables":["products"]}""";

    private const string Restricted =
        """{"id":"test-018","split":"test","family":"filter_count","template":"t","question":"How many tickets are open?","user":"restricted","answer_type":"number","answer":7,"admin_answer":9,"gold_sql":"select 1","tables":["tickets-support","tickets-security"]}""";

    private const string SeenAsOf =
        """{"id":"test-051","split":"test","family":"seen_as_of","template":"t","question":"As Connapse saw it on {as_of}, is SKU-1 below safety stock?","user":"admin","answer_type":"boolean","answer":true,"gold_sql":"select 1","tables":["inventory"],"as_of_load":1}""";

    private static AgentRun Answered(object? value) => new(
        AgentOutcome.Answered, null, [], [], TokenUsage.Zero, 1, null,
        value is null ? null : JsonSerializer.SerializeToElement(value));

    [Fact]
    public async Task CallAsync_TypedRules_RecordsTheAnswerValue()
    {
        BridgeSession session = new(new FakeTools(), TablesRagEval.Rules);

        (string _, bool isError) = await session.CallAsync("answer", Input(new { answer = new[] { "a", "b" } }), CancellationToken.None);

        isError.Should().BeFalse();
        session.Outcome.Should().Be(AgentOutcome.Answered);
        session.AnswerValue!.Value.GetRawText().Should().Be("""["a","b"]""");
        session.Answer.Should().Be("""["a","b"]""");
    }

    [Fact]
    public async Task CallAsync_TypedRulesWithNullAnswer_IsAnErrorAndLeavesTheRunOpen()
    {
        BridgeSession session = new(new FakeTools(), TablesRagEval.Rules);

        (string text, bool isError) = await session.CallAsync("answer", Input(new { answer = (object?)null }), CancellationToken.None);

        isError.Should().BeTrue();
        text.Should().Contain("Missing required argument 'answer'");
        session.Outcome.Should().BeNull();
    }

    [Fact]
    public async Task CallAsync_TypedRules_AllowsTwentyFiveCallsThenStops()
    {
        BridgeSession session = new(new FakeTools(), TablesRagEval.Rules);
        for (int i = 0; i < TablesRagEval.MaxToolCalls; i++)
            (await session.CallAsync("search_knowledge", Input(new { query = "q" }), CancellationToken.None)).IsError.Should().BeFalse();

        (string _, bool isError) = await session.CallAsync("search_knowledge", Input(new { query = "q" }), CancellationToken.None);

        isError.Should().BeTrue();
        session.Outcome.Should().Be(AgentOutcome.OutOfCalls);
        session.Calls.Should().HaveCount(25);
    }

    [Fact]
    public void Tools_TypedRules_OfferTheTypedAnswerTool()
    {
        BridgeSession session = new(new FakeTools(), TablesRagEval.Rules);

        session.Tools.Last().Should().BeSameAs(AgentContract.TypedAnswer);
        AgentContract.TypedAnswer.Required.Should().Equal("answer");
    }

    [Fact]
    public async Task CallAsync_DefaultRules_KeepTheStringAnswerAndTwelveCalls()
    {
        AgentRules.Default.Should().Be(new AgentRules(12, AgentContract.Answer, false));
        BridgeSession session = new(new FakeTools());

        (string _, bool isError) = await session.CallAsync("answer", Input(new { answer = 12 }), CancellationToken.None);

        isError.Should().BeTrue("the default answer tool still takes a string");
        session.Tools.Last().Should().BeSameAs(AgentContract.Answer);
    }

    [Fact]
    public void Parse_RestrictedQuestion_ReadsEveryField()
    {
        TableQuestion q = Question(Restricted);

        q.Restricted.Should().BeTrue();
        q.AnswerType.Should().Be("number");
        q.Answer.GetInt32().Should().Be(7);
        q.AdminAnswer!.Value.GetInt32().Should().Be(9);
        q.Tables.Should().Equal("tickets-support", "tickets-security");
        q.Documents.Should().BeEmpty();
        q.AsOfLoad.Should().BeNull();
    }

    [Fact]
    public void Ask_SeenAsOfQuestion_NamesWhenItsLoadSettled()
    {
        TableQuestion q = Question(SeenAsOf);
        Dictionary<int, DateTime> seen = new() { [1] = new DateTime(2026, 11, 9, 14, 3, 7, DateTimeKind.Utc) };

        q.Ask(seen).Should().Be("As Connapse saw it on 2026-11-09 14:03:07 UTC, is SKU-1 below safety stock?");
    }

    [Fact]
    public void Ask_LoadNeverSeen_Throws()
    {
        Action ask = () => Question(SeenAsOf).Ask(new Dictionary<int, DateTime>());

        ask.Should().Throw<InvalidDataException>().WithMessage("*load 1*");
    }

    [Fact]
    public void Ask_PlaceholderWithoutLoad_Throws()
    {
        string broken = SeenAsOf.Replace(""","as_of_load":1""", "", StringComparison.Ordinal);

        Action ask = () => Question(broken).Ask(new Dictionary<int, DateTime>());

        ask.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Ask_PlainQuestion_IsUnchanged()
    {
        Question(Lookup).Ask(new Dictionary<int, DateTime>()).Should().Be("What is the price of P-1?");
    }

    [Fact]
    public void Of_RestrictedAnswerEqualToAdminAnswer_IsWrongAndALeak()
    {
        TableGrade grade = TableGrade.Of(Question(Restricted), Answered(9));

        grade.Should().Be(new TableGrade(Correct: false, Leaked: true));
    }

    [Fact]
    public void Of_RestrictedTrimmedAnswer_IsCorrectAndNoLeak()
    {
        TableGrade grade = TableGrade.Of(Question(Restricted), Answered(7));

        grade.Should().Be(new TableGrade(Correct: true, Leaked: false));
    }

    [Fact]
    public void Of_NoAnswer_IsWrong()
    {
        AgentRun run = new(AgentOutcome.OutOfCalls, null, [], [], TokenUsage.Zero, 25, null);

        TableGrade.Of(Question(Lookup), run).Should().Be(new TableGrade(false, false));
    }

    [Fact]
    public void Corpus_LoadOne_PutsItUnderTheInventoryPath()
    {
        string root = Path.Combine(Path.GetTempPath(), "tables-rag-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (string f in new[] { "orders.csv", "inventory.load1.csv", "inventory.load2.csv" })
                File.WriteAllText(Path.Combine(root, f), f);

            List<EvalDocumentView> corpus = [.. TablesRagEval.Corpus(root, ["orders.csv", "inventory.load1.csv", "inventory.load2.csv"], "inventory.load1.csv")
                .Select(d => new EvalDocumentView(d.FileName!, d.Folder!, d.Text!))];

            corpus.Should().Equal(
                new EvalDocumentView("orders.csv", "/", "orders.csv"),
                new EvalDocumentView("inventory.csv", "/", "inventory.load1.csv"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("products.csv", true)]
    [InlineData("inventory.load2.csv", true)]
    [InlineData("orders.csv", false)]
    [InlineData("tickets-security.csv", true)]
    public void InSlice_ProductsAndTickets_KeepsThemAndInventory(string file, bool kept)
    {
        TablesRagEval.InSlice(["products", "tickets"], file).Should().Be(kept);
    }

    [Fact]
    public void InSlice_NoSlice_KeepsEverything()
    {
        TablesRagEval.InSlice(null, "orders.csv").Should().BeTrue();
    }

    private sealed record EvalDocumentView(string FileName, string Folder, string Text);

    [Fact]
    public void Report_PartialRun_IsDiagnosticOnlyAndCountsLeaks()
    {
        TablesRagEval.Options options = new("hybrid", "tune", "claude-sonnet-5-5", "medium", null, ["tune-018"], true, 1, null);
        TableQuestion q = Question(Restricted);
        AgentRun run = Answered(9);
        List<TablesRagEval.QuestionResult> results = [new(q, q.Question, run, TableGrade.Of(q, run))];

        JsonObject report = TablesRagEval.Report(options, results, []);

        report["conforming"]!.GetValue<bool>().Should().BeFalse();
        report["restrictedLeaks"]!.GetValue<int>().Should().Be(1);
        report["all"]!["correct"]!.GetValue<int>().Should().Be(0);
        report["diagnosticOnly"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain("split is tune, not test");
    }

    [Fact]
    public void RunLines_LongToolOutput_IsCutAndItsLengthKept()
    {
        TableQuestion q = Question(Lookup);
        string output = new('x', TablesRagEval.LoggedOutputChars + 10);
        AgentRun run = Answered(12.5) with { Calls = [new ToolTrace("get_document", Input(new { documentId = "d" }), output)] };

        JsonNode line = JsonNode.Parse(TablesRagEval.RunLines([new(q, q.Question, run, TableGrade.Of(q, run))]).Single())!;

        line["correct"]!.GetValue<bool>().Should().BeTrue();
        line["calls"]![0]!["outputChars"]!.GetValue<int>().Should().Be(output.Length);
        line["calls"]![0]!["output"]!.GetValue<string>().Should().HaveLength(TablesRagEval.LoggedOutputChars);
    }
}
