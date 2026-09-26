using System.Globalization;
using Connapse.Eval.Metrics;
using Connapse.Eval.Runs;
using Connapse.Eval.Statistics;

namespace Connapse.Eval.Reports;

public sealed record MetricComparison(string Dataset, string Metric, PairedComparison Stats, double HolmP, bool Significant);

public sealed record Comparison(
    string Baseline,
    string Candidate,
    IReadOnlyList<MetricComparison> Rows,
    IReadOnlyDictionary<string, double> PortfolioDelta,
    string Verdict,
    IReadOnlyList<string> DatasetMismatches);

public sealed class DatasetMismatchException(IReadOnlyList<string> datasets)
    : Exception($"Runs used different dataset versions or files for: {string.Join(", ", datasets)}. "
        + "Pass --allow-dataset-mismatch to compare anyway.");

public static class ComparisonBuilder
{
    public static Comparison Build(RunScores baseline, RunScores candidate, bool allowDatasetMismatch)
    {
        List<string> mismatches = Mismatches(baseline.Manifest, candidate.Manifest);
        if (mismatches.Count > 0 && !allowDatasetMismatch)
            throw new DatasetMismatchException(mismatches);

        List<(string Dataset, string Metric, PairedComparison Stats)> raw = [];
        foreach (DatasetScores a in baseline.Datasets.Where(d => !d.Invalid))
        {
            DatasetScores? b = candidate.Datasets.FirstOrDefault(d => d.Name == a.Name && !d.Invalid);
            if (b is null)
                continue;
            List<string> common = a.PerQuery.Keys.Intersect(b.PerQuery.Keys).Order(StringComparer.Ordinal).ToList();
            if (common.Count < 2)
                continue;
            foreach (string metric in MetricNames.Quality)
                raw.Add((a.Name, metric, PairedStats.Compare(
                    common.Select(q => a.PerQuery[q][metric]).ToList(),
                    common.Select(q => b.PerQuery[q][metric]).ToList())));
        }

        double[] holm = Holm.Adjust(raw.Select(r => r.Stats.PermutationP).ToList());
        List<MetricComparison> rows = raw
            .Select((r, i) => new MetricComparison(r.Dataset, r.Metric, r.Stats, holm[i], holm[i] < 0.05))
            .ToList();
        Dictionary<string, double> delta = MetricNames.All.ToDictionary(
            m => m, m => candidate.Portfolio[m] - baseline.Portfolio[m]);

        return new Comparison(baseline.RunName, candidate.RunName, rows, delta, Verdict(rows, delta), mismatches);
    }

    private static string Verdict(IReadOnlyList<MetricComparison> rows, IReadOnlyDictionary<string, double> delta)
    {
        List<string> drops = rows.Where(r => r.Significant && r.Stats.MeanDifference < 0)
            .Select(r => r.Dataset).Distinct().ToList();
        string change = delta[MetricNames.Ndcg10].ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture);
        if (drops.Count > 0)
            return $"Regresses on {string.Join(", ", drops)} (portfolio nDCG@10 {change})";
        return delta[MetricNames.Ndcg10] > 0
            ? $"Improves: portfolio nDCG@10 {change} with no significant drop on any dataset"
            : $"No improvement: portfolio nDCG@10 {change}";
    }

    private static List<string> Mismatches(RunManifest a, RunManifest b)
    {
        List<string> result = [];
        foreach (RunDatasetInfo x in a.Datasets)
        {
            RunDatasetInfo? y = b.Datasets.FirstOrDefault(d => d.Name == x.Name);
            if (y is null)
                continue;
            bool sameFiles = x.FileSha256.Count == y.FileSha256.Count
                && x.FileSha256.All(p => y.FileSha256.TryGetValue(p.Key, out string? h) && h == p.Value);
            if (x.Version != y.Version || !sameFiles)
                result.Add(x.Name);
        }
        return result;
    }
}
