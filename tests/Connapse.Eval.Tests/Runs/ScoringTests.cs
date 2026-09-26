using Connapse.Core;
using Connapse.Eval.Metrics;
using Connapse.Eval.Model;
using Connapse.Eval.Runs;
using Connapse.Eval.Systems;
using FluentAssertions;

namespace Connapse.Eval.Tests.Runs;

[Trait("Category", "Unit")]
public class ScoringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eval-runs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static readonly IReadOnlyDictionary<string, TimeSpan> NoStages = new Dictionary<string, TimeSpan>();

    private static QueryResult Result(string dataset, string queryId, Split split, string? error = null, params string[] docs) =>
        new(dataset, queryId, "text of " + queryId, split,
            docs.Select((d, i) => new RankedDoc(d, 1.0 - (i * 0.1))).ToList(),
            new Trace(TimeSpan.FromMilliseconds(10 * (queryId.Length + docs.Length)), NoStages), error);

    private static RunDatasetInfo Info(string name, string domain, bool invalid = false) =>
        new(name, "1", new Dictionary<string, string>(), [domain], 3, invalid ? 1 : 0, invalid, 3);

    private RunFolder WriteRun()
    {
        RunFolder run = RunFolder.Create(_root, "v1", "connapse", "hybrid", "abcdef1234", DateTimeOffset.UnixEpoch);
        Qrels qa = new();
        qa.Add("q1", "d1", 1);
        qa.Add("q2", "d2", 1);
        qa.Add("q3", "d9", 0);
        run.WriteDataset("alpha", qa, new Dictionary<string, string>(),
        [
            Result("alpha", "q1", Split.Test, null, "d1"),          // perfect
            Result("alpha", "q2", Split.Test, "boom"),               // error → scores 0
            Result("alpha", "q3", Split.Test, null, "d9"),           // no relevant → no-answer
            Result("alpha", "qd", Split.Dev, null, "zz"),            // dev split → excluded
        ]);
        run.MarkComplete("alpha");

        Qrels qb = new();
        qb.Add("q1", "d5", 1);
        run.WriteDataset("beta", qb, new Dictionary<string, string>(), [Result("beta", "q1", Split.Test, null, "x", "d5")]);
        run.MarkComplete("beta");

        run.WriteManifest(new RunManifest("abcdef1234", false, "v1", "connapse", "hybrid", "hash", SearchMode.Hybrid,
            new Dictionary<string, string>(), new Dictionary<string, string>(), "box", "os", 8,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            [Info("alpha", "domain:general"), Info("beta", "domain:engineering"), Info("gamma", "domain:general", invalid: true)]));
        return run;
    }

    [Fact]
    public void Score_Run_AveragesTestQueriesAndCountsNoAnswerAndErrors()
    {
        RunScores scores = Scoring.Score(WriteRun());

        DatasetScores alpha = scores.Datasets.Single(d => d.Name == "alpha");
        alpha.TestQueries.Should().Be(3);
        alpha.NoAnswerQueries.Should().Be(1);
        alpha.ErrorQueries.Should().Be(1);
        alpha.PerQuery.Keys.Should().BeEquivalentTo(["q1", "q2"]);
        alpha.Means[MetricNames.Mrr10].Should().Be(0.5);
    }

    [Fact]
    public void Score_Run_RollsUpDomainsAndPortfolioOverValidDatasetsOnly()
    {
        RunScores scores = Scoring.Score(WriteRun());

        scores.Datasets.Single(d => d.Name == "gamma").Invalid.Should().BeTrue();
        scores.Domains["domain:general"][MetricNames.Mrr10].Should().Be(0.5);
        scores.Domains["domain:engineering"][MetricNames.Mrr10].Should().Be(0.5);
        scores.Portfolio[MetricNames.Mrr10].Should().Be(0.5);
        scores.Portfolio[MetricNames.Hit1].Should().Be(0.25);
    }

    [Fact]
    public void Percentile_NearestRank_MatchesDefinition()
    {
        Scoring.Percentile([5, 1, 3, 2, 4], 50).Should().Be(3);
        Scoring.Percentile([5, 1, 3, 2, 4], 95).Should().Be(5);
        Scoring.Percentile([], 50).Should().Be(0);
    }

    [Fact]
    public void ReadResults_AfterWrite_RoundTrips()
    {
        RunFolder run = WriteRun();

        IReadOnlyList<QueryResult> results = RunFolder.Open(run.Path).ReadResults("beta");

        results.Should().ContainSingle().Which.Ranked.Select(r => r.DocId).Should().Equal("x", "d5");
        File.ReadAllText(System.IO.Path.Combine(run.Path, "trec", "beta.trec"))
            .Should().StartWith("q1 Q0 x 1 ");
    }
}
