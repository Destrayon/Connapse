using Connapse.Core;
using Connapse.Eval.Metrics;
using Connapse.Eval.Reports;
using Connapse.Eval.Runs;
using FluentAssertions;

namespace Connapse.Eval.Tests.Reports;

[Trait("Category", "Unit")]
public class ComparisonTests
{
    internal static RunScores Scores(string runName, Func<string, int, double> ndcg, string version = "1", params string[] datasets)
    {
        List<DatasetScores> list = datasets.Select(name =>
        {
            Dictionary<string, IReadOnlyDictionary<string, double>> perQuery = Enumerable.Range(0, 30).ToDictionary(
                i => $"q{i}",
                i => (IReadOnlyDictionary<string, double>)MetricNames.All.ToDictionary(m => m, m => ndcg(name, i)));
            Dictionary<string, double> means = MetricNames.All.ToDictionary(m => m, m => perQuery.Values.Average(v => v[m]));
            return new DatasetScores(name, ["domain:general"], false, 30, 0, 0, means, perQuery, 10, 20);
        }).ToList();
        RunManifest manifest = new("sha", false, "v1", "connapse", "cfg", "h", SearchMode.Hybrid,
            new Dictionary<string, string>(), new Dictionary<string, string>(), "box", "os", 8,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            datasets.Select(d => new RunDatasetInfo(d, version, new Dictionary<string, string> { ["f"] = "abc" },
                ["domain:general"], 10, 0, false, 30)).ToList());
        Dictionary<string, double> portfolio = MetricNames.All.ToDictionary(m => m, m => list.Average(d => d.Means[m]));
        return new RunScores(runName, manifest, list, new Dictionary<string, IReadOnlyDictionary<string, double>>(), portfolio);
    }

    private static double Base(string dataset, int i) => 0.3 + ((i % 5) * 0.1);

    [Fact]
    public void Build_IdenticalRuns_FindsNothingSignificant()
    {
        Comparison c = ComparisonBuilder.Build(Scores("a", Base, "1", "x", "y"), Scores("b", Base, "1", "x", "y"), false);

        c.Rows.Should().OnlyContain(r => !r.Significant);
        c.Verdict.Should().StartWith("No improvement");
    }

    [Fact]
    public void Build_CandidateBetterEverywhere_Improves()
    {
        Comparison c = ComparisonBuilder.Build(
            Scores("a", Base, "1", "x", "y"), Scores("b", (d, i) => Base(d, i) + 0.1 + ((i % 3) * 0.01), "1", "x", "y"), false);

        c.Rows.Where(r => r.Metric == MetricNames.Ndcg10).Should().OnlyContain(r => r.Significant && r.Stats.MeanDifference > 0);
        c.Verdict.Should().StartWith("Improves");
    }

    [Fact]
    public void Build_OneDatasetDrops_NamesIt()
    {
        Comparison c = ComparisonBuilder.Build(
            Scores("a", Base, "1", "x", "y"),
            Scores("b", (d, i) => d == "y" ? Base(d, i) - 0.2 - ((i % 3) * 0.01) : Base(d, i) + 0.3, "1", "x", "y"), false);

        c.Verdict.Should().StartWith("Regresses on y");
    }

    [Fact]
    public void Build_DifferentDatasetVersions_ThrowsUnlessAllowed()
    {
        RunScores a = Scores("a", Base, "1", "x");
        RunScores b = Scores("b", Base, "2", "x");

        Action strict = () => ComparisonBuilder.Build(a, b, false);
        strict.Should().Throw<DatasetMismatchException>().WithMessage("*x*");
        ComparisonBuilder.Build(a, b, true).DatasetMismatches.Should().ContainSingle();
    }

    [Fact]
    public void Build_CandidateMissingDataset_ExcludesItFromPortfolioDeltaAndNamesItUnpaired()
    {
        Comparison c = ComparisonBuilder.Build(Scores("a", Base, "1", "x", "y"), Scores("b", Base, "1", "x"), false);

        c.PortfolioDelta[MetricNames.Ndcg10].Should().Be(0);
        c.UnpairedDatasets.Should().Equal("y");
        c.Verdict.Should().NotStartWith("Improves").And.Contain("not compared: y");
    }
}
