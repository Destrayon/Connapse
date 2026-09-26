using System.Text.Json;
using Connapse.Eval.Metrics;
using Connapse.Eval.Model;
using FluentAssertions;

namespace Connapse.Eval.Tests.Metrics;

[Trait("Category", "Unit")]
public class TrecEvalFixtureTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "trec");

    public static TheoryData<string> QueryIds => new() { "q1", "q2", "q3", "q4", "q5", "q6" };

    [Theory]
    [MemberData(nameof(QueryIds))]
    public void Score_FixtureQuery_MatchesTrecEval(string queryId)
    {
        Qrels qrels = Qrels.ParseTrec(new StringReader(File.ReadAllText(Path.Combine(Dir, "qrels.txt"))));
        List<RankedDoc> ranked = File.ReadAllLines(Path.Combine(Dir, "run.txt"))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(f => f[0] == queryId)
            .Select(f => new RankedDoc(f[2], double.Parse(f[4], System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();
        Dictionary<string, Dictionary<string, double>> expected =
            JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(
                File.ReadAllText(Path.Combine(Dir, "expected.json")))!;

        IReadOnlyDictionary<string, double>? scores = RankingMetrics.Score(ranked, qrels.For(queryId));

        scores.Should().NotBeNull();
        foreach ((string metric, double value) in expected[queryId])
            scores![metric].Should().BeApproximately(value, 1e-9, $"{metric} for {queryId}");
    }
}
