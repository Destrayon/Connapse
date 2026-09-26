using Connapse.Eval.Metrics;
using Connapse.Eval.Model;

namespace Connapse.Eval.Runs;

public sealed record DatasetScores(
    string Name,
    IReadOnlyList<string> Tags,
    bool Invalid,
    int TestQueries,
    int NoAnswerQueries,
    int ErrorQueries,
    IReadOnlyDictionary<string, double> Means,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> PerQuery,
    double LatencyP50Ms,
    double LatencyP95Ms);

public sealed record RunScores(
    string RunName,
    RunManifest Manifest,
    IReadOnlyList<DatasetScores> Datasets,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> Domains,
    IReadOnlyDictionary<string, double> Portfolio);

public static class Scoring
{
    private static readonly IReadOnlyDictionary<string, double> NoMeans = new Dictionary<string, double>();
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> NoQueries =
        new Dictionary<string, IReadOnlyDictionary<string, double>>();

    public static RunScores Score(RunFolder run)
    {
        RunManifest manifest = run.ReadManifest();
        List<DatasetScores> datasets = [];

        foreach (RunDatasetInfo info in manifest.Datasets)
        {
            if (info.Invalid || !run.IsDatasetComplete(info.Name))
            {
                datasets.Add(new DatasetScores(info.Name, info.Tags, true, 0, 0, 0, NoMeans, NoQueries, 0, 0));
                continue;
            }

            Qrels qrels = run.ReadQrels(info.Name);
            IReadOnlyList<QueryResult> all = run.ReadResults(info.Name);
            List<QueryResult> test = all.Where(r => r.Split == Split.Test).ToList();
            Dictionary<string, IReadOnlyDictionary<string, double>> perQuery = new(StringComparer.Ordinal);
            int noAnswer = 0;
            foreach (QueryResult result in test)
            {
                IReadOnlyDictionary<string, double>? scores = RankingMetrics.Score(result.Ranked, qrels.For(result.QueryId));
                if (scores is null)
                    noAnswer++;
                else
                    perQuery[result.QueryId] = scores;
            }

            Dictionary<string, double> means = MetricNames.All.ToDictionary(
                m => m, m => perQuery.Count == 0 ? double.NaN : perQuery.Values.Average(v => v[m]));
            List<double> latencies = test.Select(r => r.Trace.Total.TotalMilliseconds).ToList();
            datasets.Add(new DatasetScores(info.Name, info.Tags, false, test.Count, noAnswer,
                test.Count(r => r.Error is not null), means, perQuery, Percentile(latencies, 50), Percentile(latencies, 95)));
        }

        List<DatasetScores> valid = datasets.Where(d => !d.Invalid && d.PerQuery.Count > 0).ToList();
        Dictionary<string, IReadOnlyDictionary<string, double>> domains = valid
            .SelectMany(d => d.Tags.Where(t => t.StartsWith("domain:", StringComparison.Ordinal)).Select(t => (Tag: t, Dataset: d)))
            .GroupBy(x => x.Tag)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, double>)MeanOver(g.Select(x => x.Dataset)));

        return new RunScores(run.Name, manifest, datasets, domains, MeanOver(valid));
    }

    /// <summary>Nearest-rank percentile; 0 for an empty list.</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0)
            return 0;
        List<double> sorted = values.Order().ToList();
        int index = Math.Clamp((int)Math.Ceiling(p / 100 * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    private static Dictionary<string, double> MeanOver(IEnumerable<DatasetScores> datasets)
    {
        List<DatasetScores> list = datasets.ToList();
        return MetricNames.All.ToDictionary(m => m, m => list.Count == 0 ? double.NaN : list.Average(d => d.Means[m]));
    }
}
