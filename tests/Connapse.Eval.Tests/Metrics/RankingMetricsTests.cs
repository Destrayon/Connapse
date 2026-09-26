using Connapse.Eval.Metrics;
using Connapse.Eval.Model;
using FluentAssertions;

namespace Connapse.Eval.Tests.Metrics;

[Trait("Category", "Unit")]
public class RankingMetricsTests
{
    private static readonly Dictionary<string, int> OneRelevant = new() { ["d1"] = 1, ["d2"] = 0 };

    [Fact]
    public void Score_EmptyRun_ScoresZeroEverywhere()
    {
        IReadOnlyDictionary<string, double>? scores = RankingMetrics.Score([], OneRelevant);

        scores.Should().NotBeNull();
        MetricNames.All.Should().OnlyContain(m => scores![m] == 0.0);
    }

    [Fact]
    public void Score_NoRelevantJudgment_ReturnsNull()
    {
        IReadOnlyDictionary<string, double>? scores =
            RankingMetrics.Score([new RankedDoc("d2", 1.0)], new Dictionary<string, int> { ["d2"] = 0 });

        scores.Should().BeNull();
    }

    [Fact]
    public void Score_DuplicateDocument_Throws()
    {
        Action act = () => RankingMetrics.Score([new RankedDoc("d1", 1.0), new RankedDoc("d1", 0.5)], OneRelevant);

        act.Should().Throw<DuplicateDocumentException>().WithMessage("*d1*");
    }

    [Fact]
    public void Score_HalfOfTopJudged_ReportsJudgedFraction()
    {
        IReadOnlyDictionary<string, double>? scores =
            RankingMetrics.Score([new RankedDoc("d1", 1.0), new RankedDoc("unjudged", 0.5)], OneRelevant);

        scores![MetricNames.Judged10].Should().Be(0.5);
    }

    [Fact]
    public void Score_RelevantDocBeyondRankTen_IsNotCounted()
    {
        List<RankedDoc> ranked = Enumerable.Range(0, 10)
            .Select(i => new RankedDoc($"x{i}", 1.0 - (i * 0.01)))
            .Append(new RankedDoc("d1", 0.0))
            .ToList();

        IReadOnlyDictionary<string, double>? scores = RankingMetrics.Score(ranked, OneRelevant);

        scores![MetricNames.Mrr10].Should().Be(0.0);
        scores[MetricNames.Recall10].Should().Be(0.0);
    }

    [Fact]
    public void Order_TiedScores_BreaksTiesByDocumentIdDescending()
    {
        IReadOnlyList<RankedDoc> ordered =
            RankingMetrics.Order([new RankedDoc("a", 0.5), new RankedDoc("c", 0.5), new RankedDoc("b", 0.9)]);

        ordered.Select(r => r.DocId).Should().Equal("b", "c", "a");
    }
}
