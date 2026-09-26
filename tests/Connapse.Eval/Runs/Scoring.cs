using Connapse.Eval.Metrics;
using Connapse.Eval.Model;

namespace Connapse.Eval.Runs;

/// <param name="Invalid">True when the dataset is not scored; <paramref name="NotScoredReason"/> says why.</param>
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
    double LatencyP95Ms,
    string? NotScoredReason);

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
            string? notScored = info.Invalid ? "more than 1% of documents failed to ingest"
                : !run.IsDatasetComplete(info.Name) ? "did not finish (no .done marker)"
                : null;
            if (notScored is not null)
            {
                datasets.Add(new DatasetScores(info.Name, info.Tags, true, 0, 0, 0, NoMeans, NoQueries, 0, 0, notScored));
                continue;
            }

            Qrels qrels = run.ReadQrels(info.Name);
            IReadOnlyList<QueryResult> all = run.ReadResults(info.Name);
            HashSet<string> seenQueryIds = new(StringComparer.Ordinal);
            foreach (QueryResult result in all)
                if (!seenQueryIds.Add(result.QueryId))
                    throw new InvalidDataException($"Dataset '{info.Name}' results file contains duplicate query ID '{result.QueryId}'.");
            List<QueryResult> test = all.Where(r => r.Split == Split.Test).ToList();
            Dictionary<string, IReadOnlyDictionary<string, double>> perQuery = new(StringComparer.Ordinal);
            int noAnswer = 0;
            foreach (QueryResult result in test)
            {
                // An errored result's ranking is not trustworthy (the search may have failed before
                // producing it, or partway through), so score it as an empty ranking rather than
                // whatever it happened to return.
                IReadOnlyList<RankedDoc> scored = result.Error is null ? result.Ranked : [];
                IReadOnlyDictionary<string, double>? scores = RankingMetrics.Score(scored, qrels.For(result.QueryId));
                if (scores is null)
                    noAnswer++;
                else
                    perQuery[result.QueryId] = scores;
            }

            Dictionary<string, double> means = MetricNames.All.ToDictionary(
                m => m, m => perQuery.Count == 0 ? double.NaN : perQuery.Values.Average(v => v[m]));
            List<double> latencies = test.Select(r => r.Trace.Total.TotalMilliseconds).ToList();
            datasets.Add(new DatasetScores(info.Name, info.Tags, false, test.Count, noAnswer,
                test.Count(r => r.Error is not null), means, perQuery, Percentile(latencies, 50), Percentile(latencies, 95), null));
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
