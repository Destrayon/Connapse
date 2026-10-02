using Connapse.Eval.Metrics;
using Connapse.Eval.Model;
using Connapse.Eval.Systems;
using FluentAssertions;

namespace Connapse.Eval.Tests.Metrics;

[Trait("Category", "Unit")]
public class PassageJudgeTests
{
    private static readonly PassageGold Gold = new("report.pdf", ["Ohio", "4,213"], "4,213");

    [Theory]
    [InlineData("| Ohio | 4,213 | 12.5% |", true)]
    [InlineData("OHIO  4213", true)]
    [InlineData("**Ohio**: 4 213", false)]
    [InlineData("Ohio 14,213", false)]
    [InlineData("Ohio.\n4,213.", true)]
    public void ContainsAll_IgnoresMarkupCaseAndSeparators_MatchesWholeWords(string text, bool expected)
    {
        List<string> evidence = Gold.Evidence.Select(PassageJudge.Normalize).ToList();

        PassageJudge.ContainsAll(text, evidence).Should().Be(expected);
    }

    [Fact]
    public void Judge_EvidenceScatteredFurtherThanTheGapAllows_NotRelevant()
    {
        PassageGold tight = Gold with { MaxGapWords = 3 };
        List<RetrievedPassage> passages =
        [
            new("report.pdf", "scrambled", "Ohio | Texas | Maine | Iowa | 12 | 31 | 4,213"),
            new("report.pdf", "row", "Texas 9,001 | Ohio 812 4,213"),
        ];

        (_, IReadOnlyDictionary<string, int> judgments) = PassageJudge.Judge(tight, passages);

        judgments["report.pdf#scrambled"].Should().Be(0);
        judgments["report.pdf#row"].Should().Be(1);
    }

    [Fact]
    public void ContainsWithin_MultiWordEvidence_GapExcludesEvidenceWords()
    {
        List<string> evidence = new[] { "not a citizen", "55,660" }.Select(PassageJudge.Normalize).ToList();

        PassageJudge.ContainsWithin("Not a citizen 9,375 55,660", evidence, 1).Should().BeTrue();
        PassageJudge.ContainsWithin("Not a citizen 9,375 1,564 55,660", evidence, 1).Should().BeFalse();
    }

    [Fact]
    public void Normalize_LineBreakHyphenationAndLigature_Joined()
    {
        PassageJudge.Normalize("the ﬁscal assess-\nment").Should().Be("the fiscal assessment");
    }

    [Fact]
    public void Normalize_DecimalPoint_Kept()
    {
        PassageJudge.Normalize("Rate: 3.75%.").Should().Be("rate 3.75");
    }

    [Fact]
    public void Judge_FirstMatchingGoldChunkIsTheOnlyRelevantPassage()
    {
        List<RetrievedPassage> passages =
        [
            new("other.pdf", "c1", "Ohio 4,213"),
            new("report.pdf", "c2", "Ohio only"),
            new("report.pdf", "c3", "Ohio | 4,213"),
            new("report.pdf", "c4", "overlap: Ohio 4,213"),
        ];

        (IReadOnlyList<RankedDoc> ranked, IReadOnlyDictionary<string, int> judgments) = PassageJudge.Judge(Gold, passages);

        IReadOnlyDictionary<string, double> scores = RankingMetrics.Score(ranked, judgments)!;
        ranked.Select(r => r.DocId).Should().Equal("other.pdf#c1", "report.pdf#c2", "report.pdf#c3", "report.pdf#c4");
        judgments.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["report.pdf#c2"] = 0, ["report.pdf#c3"] = 1, ["report.pdf#c4"] = 0,
        });
        scores[MetricNames.Mrr10].Should().BeApproximately(1.0 / 3, 1e-9);
        scores[MetricNames.Recall5].Should().Be(1);
    }

    [Fact]
    public void Judge_NoMatchingChunk_ScoresZeroInsteadOfDroppingTheQuery()
    {
        (IReadOnlyList<RankedDoc> ranked, IReadOnlyDictionary<string, int> judgments) =
            PassageJudge.Judge(Gold, [new RetrievedPassage("report.pdf", "c1", "Ohio")]);

        judgments.Should().ContainKey("report.pdf#gold").WhoseValue.Should().Be(1);
        RankingMetrics.Score(ranked, judgments)![MetricNames.Recall10].Should().Be(0);
    }

    [Fact]
    public void DocumentOf_PassageId_ReturnsTheDocument()
    {
        PassageJudge.DocumentOf("a#b.pdf#chunk-1").Should().Be("a#b.pdf");
    }
}
