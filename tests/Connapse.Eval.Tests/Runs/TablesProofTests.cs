using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Eval.Runs;
using Connapse.Eval.Runs.MemorySearch;
using FluentAssertions;

namespace Connapse.Eval.Tests.Runs;

[Trait("Category", "Unit")]
public class TablesProofTests
{
    // Fixtures are scipy.stats.binom.cdf(min(b, c), b + c, 0.5) * 2, capped at 1, as statsmodels' exact mcnemar computes.
    [Theory]
    [InlineData(0, 0, 1.0)]
    [InlineData(5, 5, 1.0)]
    [InlineData(1, 9, 22.0 / 1024)]
    [InlineData(9, 1, 22.0 / 1024)]
    [InlineData(2, 20, 508.0 / 4194304)]
    [InlineData(0, 10, 2.0 / 1024)]
    public void McNemarExactP_KnownTables_MatchesStatsmodels(int b, int c, double expected) =>
        TablesProof.McNemarExactP(b, c).Should().BeApproximately(expected, 1e-12);

    [Fact]
    public void McNemarExactP_LargeOneSidedTable_DoesNotUnderflowToNaN()
    {
        double p = TablesProof.McNemarExactP(0, 200);

        p.Should().BeGreaterThan(0).And.BeLessThan(1e-50);
        double.IsNaN(p).Should().BeFalse();
    }

    [Fact]
    public void McNemarExactP_NegativeCount_Throws()
    {
        Action first = () => TablesProof.McNemarExactP(-1, 0);
        Action second = () => TablesProof.McNemarExactP(0, -1);

        first.Should().Throw<ArgumentOutOfRangeException>();
        second.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BinomialHalfCdf_WholeRange_IsOne() =>
        TablesProof.BinomialHalfCdf(10, 10).Should().BeApproximately(1, 1e-12);

    [Fact]
    public void GradedRun_Parse_ReadsFieldsAndDefaultsCost()
    {
        GradedRun withCost = GradedRun.Parse("""{"id":"test-001","family":"join","correct":true,"costUsd":0.25}""");
        GradedRun withoutCost = GradedRun.Parse("""{"id":"mixed-002","family":"mixed","correct":false}""");

        withCost.Should().Be(new GradedRun("test-001", "join", true, 0.25));
        withCost.Mixed.Should().BeFalse();
        withoutCost.CostUsd.Should().Be(0);
        withoutCost.Mixed.Should().BeTrue();
    }

    [Fact]
    public void GradedRun_ReadAll_SkipsBlankLines()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"id":"test-001","family":"join","correct":true,"costUsd":0.1}""" + "\n\n");

            GradedRun.ReadAll(path).Should().ContainSingle().Which.Id.Should().Be("test-001");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Compare_TablesClearlyBetter_PassesEveryAgentBar()
    {
        List<GradedRun> rag = [];
        List<GradedRun> tables = [];
        for (int i = 0; i < 100; i++)
        {
            string family = i % 2 == 0 ? "lookup" : "join";
            rag.Add(new GradedRun($"test-{i:000}", family, i < 20, 0.10));
            tables.Add(new GradedRun($"test-{i:000}", family, i < 90, 0.12));
        }
        for (int i = 0; i < 4; i++)
        {
            rag.Add(new GradedRun($"mixed-{i:000}", "mixed", false, 0.10));
            tables.Add(new GradedRun($"mixed-{i:000}", "mixed", i < 2, 0.12));
        }

        JsonObject result = TablesProof.Compare(rag, tables);

        result["questions"]!.GetValue<int>().Should().Be(100);
        result["bar1Accuracy"]!["gainPoints"]!.GetValue<double>().Should().Be(70);
        result["bar1Accuracy"]!["onlyTablesRight"]!.GetValue<int>().Should().Be(70);
        result["bar1Accuracy"]!["onlyRagRight"]!.GetValue<int>().Should().Be(0);
        result["bar1Accuracy"]!["pass"]!.GetValue<bool>().Should().BeTrue();
        result["bar2AbsoluteAccuracy"]!["accuracy"]!.GetValue<double>().Should().Be(0.9);
        result["bar2AbsoluteAccuracy"]!["pass"]!.GetValue<bool>().Should().BeTrue();
        result["bar3EveryFamily"]!["pass"]!.GetValue<bool>().Should().BeTrue();
        result["bar3EveryFamily"]!["families"]!["join"]!["questions"]!.GetValue<int>().Should().Be(50);
        result["bar4Mixed"]!["questions"]!.GetValue<int>().Should().Be(4);
        result["bar4Mixed"]!["both"]!.GetValue<int>().Should().Be(2);
        result["bar4Mixed"]!["pass"]!.GetValue<bool>().Should().BeTrue();
        result["bar7AgentCost"]!["ratio"]!.GetValue<double>().Should().Be(1.2);
        result["bar7AgentCost"]!["pass"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Compare_TablesWorseInOneFamilyAndCostly_FailsThoseBars()
    {
        List<GradedRun> rag =
        [
            new("test-001", "lookup", true, 0.10),
            new("test-002", "lookup", true, 0.10),
            new("test-003", "join", false, 0.10),
            new("mixed-001", "mixed", true, 0.10),
        ];
        List<GradedRun> tables =
        [
            new("test-001", "lookup", false, 0.20),
            new("test-002", "lookup", true, 0.20),
            new("test-003", "join", true, 0.20),
            new("mixed-001", "mixed", false, 0.20),
        ];

        JsonObject result = TablesProof.Compare(rag, tables);

        result["bar1Accuracy"]!["gainPoints"]!.GetValue<double>().Should().Be(0);
        result["bar1Accuracy"]!["pass"]!.GetValue<bool>().Should().BeFalse();
        result["bar2AbsoluteAccuracy"]!["pass"]!.GetValue<bool>().Should().BeFalse();
        result["bar3EveryFamily"]!["families"]!["lookup"]!["pass"]!.GetValue<bool>().Should().BeFalse();
        result["bar3EveryFamily"]!["families"]!["join"]!["pass"]!.GetValue<bool>().Should().BeTrue();
        result["bar3EveryFamily"]!["pass"]!.GetValue<bool>().Should().BeFalse();
        result["bar4Mixed"]!["pass"]!.GetValue<bool>().Should().BeFalse();
        result["bar7AgentCost"]!["ratio"]!.GetValue<double>().Should().Be(2);
        result["bar7AgentCost"]!["pass"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void Compare_BigGainButNotSignificant_FailsBar1()
    {
        // 3 of 4 questions flip to right: 75 points, but McNemar p on (0, 3) is 0.25.
        List<GradedRun> rag = [.. Enumerable.Range(0, 4).Select(i => new GradedRun($"test-{i}", "sum_avg", i == 0, 0.1))];
        List<GradedRun> tables = [.. Enumerable.Range(0, 4).Select(i => new GradedRun($"test-{i}", "sum_avg", true, 0.1))];

        JsonObject result = TablesProof.Compare(rag, tables);

        result["bar1Accuracy"]!["gainPoints"]!.GetValue<double>().Should().Be(75);
        result["bar1Accuracy"]!["mcnemarP"]!.GetValue<double>().Should().BeApproximately(0.25, 1e-12);
        result["bar1Accuracy"]!["pass"]!.GetValue<bool>().Should().BeFalse();
        result["bar2AbsoluteAccuracy"]!["pass"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Compare_NoMixedAndFreeRag_FailsMixedAndHasNoCostRatio()
    {
        List<GradedRun> rag = [new("test-1", "lookup", false, 0)];
        List<GradedRun> tables = [new("test-1", "lookup", true, 0.1)];

        JsonObject result = TablesProof.Compare(rag, tables);

        result["bar4Mixed"]!["pass"]!.GetValue<bool>().Should().BeFalse();
        result["bar7AgentCost"]!["ratio"].Should().BeNull();
        result["bar7AgentCost"]!["pass"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void Compare_DifferentQuestions_Throws()
    {
        Action act = () => TablesProof.Compare([new("test-1", "lookup", true, 0)], [new("test-2", "lookup", true, 0)]);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Compare_OnlyMixedQuestions_Throws()
    {
        Action act = () => TablesProof.Compare([new("mixed-1", "mixed", true, 0)], [new("mixed-1", "mixed", true, 0)]);

        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(TablesRagEval.RagSetup, new[] { "search_knowledge", "get_document" })]
    [InlineData(TablesRagEval.TablesSetup, new[] { "table_list", "table_describe", "table_read", "table_aggregate" })]
    [InlineData(TablesRagEval.BothSetup, new[] { "search_knowledge", "get_document", "table_list", "table_describe", "table_read", "table_aggregate" })]
    public void ToolsOf_KnownSetup_ReturnsItsTools(string setup, string[] expected) =>
        TablesRagEval.ToolsOf(setup).Should().BeEquivalentTo(expected);

    [Fact]
    public void ToolsOf_UnknownSetup_Throws()
    {
        Action act = () => TablesRagEval.ToolsOf("sql");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(TablesRagEval.RagSetup, TablesRagEval.RagSetup)]
    [InlineData(TablesRagEval.TablesSetup, TablesRagEval.BothSetup)]
    [InlineData(TablesRagEval.BothSetup, TablesRagEval.BothSetup)]
    public void MixedSetupOf_Setup_AsksMixedWithRagOrBoth(string setup, string expected) =>
        TablesRagEval.MixedSetupOf(setup).Should().Be(expected);

    [Fact]
    public void JsonType_TableToolParameterTypes_MapToStringAndObject()
    {
        McpToolbox.JsonType(typeof(Guid)).Should().Be("string");
        McpToolbox.JsonType(typeof(Guid?)).Should().Be("string");
        McpToolbox.JsonType(typeof(JsonElement)).Should().Be("object");
    }

    [Fact]
    public void Describe_TableTools_OfferEveryAgentParameter()
    {
        foreach (string tool in TablesRagEval.TableTools)
        {
            ToolSpec spec = McpToolbox.Describe(McpToolbox.Find(tool));

            spec.Name.Should().Be(tool);
            spec.Description.Should().NotBeEmpty();
        }
    }

    [Theory]
    [InlineData("table_read")]
    [InlineData("table_aggregate")]
    public void Describe_ReadAndAggregate_TellTheAgentAboutSeenAsOf(string tool) =>
        McpToolbox.Describe(McpToolbox.Find(tool)).Description.Should().Contain("seen_as_of");
}
