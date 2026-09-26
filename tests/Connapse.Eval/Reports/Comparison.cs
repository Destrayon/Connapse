using System.Globalization;
using Connapse.Eval.Metrics;
using Connapse.Eval.Runs;
using Connapse.Eval.Statistics;

namespace Connapse.Eval.Reports;

public sealed record MetricComparison(string Dataset, string Metric, PairedComparison Stats, double HolmP, bool Significant);

/// <summary>How many scored test queries each run has for a dataset, and how many both share.</summary>
public sealed record DatasetPairing(
    string Dataset,
    int BaselineQueries,
    int CandidateQueries,
    int PairedQueries,
    double BaselineJudgedAt10,
    double CandidateJudgedAt10);

public sealed record Comparison(
    string Baseline,
    string Candidate,
    IReadOnlyList<MetricComparison> Rows,
    IReadOnlyDictionary<string, double> PortfolioDelta,
    string Verdict,
    IReadOnlyList<string> DatasetMismatches,
    IReadOnlyList<string> UnpairedDatasets,
    IReadOnlyList<DatasetPairing> Pairings);

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

        HashSet<string> baselineValid = baseline.Datasets
            .Where(d => !d.Invalid && d.PerQuery.Count > 0).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        HashSet<string> candidateValid = candidate.Datasets
            .Where(d => !d.Invalid && d.PerQuery.Count > 0).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        List<string> paired = baselineValid.Intersect(candidateValid).Order(StringComparer.Ordinal).ToList();
        List<string> unpaired = baseline.Datasets.Select(d => d.Name)
            .Union(candidate.Datasets.Select(d => d.Name), StringComparer.Ordinal)
            .Except(paired, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // Each paired dataset contributes the mean of per-query differences over the queries both runs scored,
        // so a query one run skipped cannot move the delta.
        List<DatasetPairing> pairings = [];
        List<Dictionary<string, double>> datasetDeltas = [];
        foreach (string name in paired)
        {
            DatasetScores a = baseline.Datasets.First(d => d.Name == name);
            DatasetScores b = candidate.Datasets.First(d => d.Name == name);
            List<string> common = a.PerQuery.Keys.Intersect(b.PerQuery.Keys).ToList();
            pairings.Add(new DatasetPairing(name, a.PerQuery.Count, b.PerQuery.Count, common.Count,
                a.Means[MetricNames.Judged10], b.Means[MetricNames.Judged10]));
            if (common.Count >= 1)
                datasetDeltas.Add(MetricNames.All.ToDictionary(
                    m => m, m => common.Average(q => b.PerQuery[q][m] - a.PerQuery[q][m])));
        }

        Dictionary<string, double> delta = MetricNames.All.ToDictionary(m => m, m => datasetDeltas.Count == 0
            ? double.NaN
            : datasetDeltas.Average(d => d[m]));

        return new Comparison(baseline.RunName, candidate.RunName, rows, delta,
            Verdict(rows, delta, unpaired, PartialOverlap(pairings)), mismatches, unpaired, pairings);
    }

    /// <summary>Paired datasets where fewer queries are shared than at least one run scored.</summary>
    public static IReadOnlyList<string> PartialOverlap(IEnumerable<DatasetPairing> pairings) =>
        pairings.Where(p => p.PairedQueries < p.BaselineQueries || p.PairedQueries < p.CandidateQueries)
            .Select(p => p.Dataset).ToList();

    private static string Verdict(IReadOnlyList<MetricComparison> rows, IReadOnlyDictionary<string, double> delta,
        IReadOnlyList<string> unpaired, IReadOnlyList<string> partial)
    {
        string suffix = (unpaired.Count > 0 ? $" — not compared: {string.Join(", ", unpaired)}" : "")
            + (partial.Count > 0 ? $" — partial query overlap: {string.Join(", ", partial)}" : "");
        if (double.IsNaN(delta[MetricNames.Ndcg10]))
            return $"No paired datasets to compare{suffix}";
        List<string> drops = rows.Where(r => r.Significant && r.Stats.MeanDifference < 0)
            .Select(r => r.Dataset).Distinct().ToList();
        string change = delta[MetricNames.Ndcg10].ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture);
        if (drops.Count > 0)
            return $"Regresses on {string.Join(", ", drops)} (portfolio nDCG@10 {change}){suffix}";
        return delta[MetricNames.Ndcg10] > 0
            ? $"Improves: portfolio nDCG@10 {change} with no significant drop on any dataset{suffix}"
            : $"No improvement: portfolio nDCG@10 {change}{suffix}";
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
