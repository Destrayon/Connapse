using Connapse.Eval.Checks;
using Connapse.Eval.Runs;
using Connapse.Eval.Statistics;

namespace Connapse.Eval.Reports;

/// <param name="Stats">Paired over checks both runs scored (pass = 1, fail = 0); null when fewer than two pairs.</param>
public sealed record CategoryComparison(
    string Dataset, string Category, string Level, double BaselineRate, double CandidateRate,
    PairedComparison? Stats, double HolmP, bool Significant);

public sealed record ExtractionComparisonResult(
    string Baseline,
    string Candidate,
    IReadOnlyList<CategoryComparison> Rows,
    double SilentFailureDelta,
    double FailsLoudlyDelta,
    string Verdict,
    IReadOnlyList<string> UnpairedDatasets);

/// <summary>
/// Compares two extract runs check by check: per dataset, category and level, the paired
/// permutation test on 0/1 outcomes, Holm-corrected across all rows, as ranking comparisons do.
/// </summary>
public static class ExtractionComparisonBuilder
{
    public static ExtractionComparisonResult Build(RunFolder baselineRun, RunFolder candidateRun)
    {
        ExtractionScores baseline = ExtractionScoring.Score(baselineRun);
        ExtractionScores candidate = ExtractionScoring.Score(candidateRun);
        HashSet<string> baseComplete = baseline.Datasets.Where(d => d.Complete).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        HashSet<string> candComplete = candidate.Datasets.Where(d => d.Complete).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        List<string> shared = baseComplete.Intersect(candComplete).Order(StringComparer.Ordinal).ToList();
        List<string> unpaired = baseComplete.Union(candComplete).Except(shared).Order(StringComparer.Ordinal).ToList();

        List<(string Dataset, string Category, string Level, List<double> Base, List<double> Cand)> groups = [];
        foreach (string dataset in shared)
        {
            Dictionary<(string, string), CheckRecord> candidateChecks = candidateRun.ReadChecks(dataset)
                .Where(c => c.Level != CheckLevel.Document && c.Outcome != CheckOutcome.Skipped)
                .ToDictionary(c => (c.CheckId, c.Level));
            foreach (IGrouping<(string Category, string Level), CheckRecord> group in baselineRun.ReadChecks(dataset)
                .Where(c => c.Level != CheckLevel.Document && c.Outcome != CheckOutcome.Skipped)
                .GroupBy(c => (c.Category, c.Level))
                .OrderBy(g => g.Key.Category, StringComparer.Ordinal).ThenBy(g => g.Key.Level, StringComparer.Ordinal))
            {
                List<double> b = [], c = [];
                foreach (CheckRecord check in group)
                    if (candidateChecks.TryGetValue((check.CheckId, check.Level), out CheckRecord? other))
                    {
                        b.Add(check.Outcome == CheckOutcome.Pass ? 1 : 0);
                        c.Add(other.Outcome == CheckOutcome.Pass ? 1 : 0);
                    }
                groups.Add((dataset, group.Key.Category, group.Key.Level, b, c));
            }
        }

        List<PairedComparison?> stats = groups.Select(g => g.Base.Count >= 2 ? PairedStats.Compare(g.Base, g.Cand) : null).ToList();
        List<int> tested = Enumerable.Range(0, groups.Count).Where(i => stats[i] is not null).ToList();
        double[] holm = Holm.Adjust(tested.Select(i => stats[i]!.PermutationP).ToList());
        Dictionary<int, double> holmByRow = tested.Select((row, k) => (row, holm[k])).ToDictionary(x => x.row, x => x.Item2);

        List<CategoryComparison> rows = groups.Select((g, i) =>
        {
            double p = holmByRow.GetValueOrDefault(i, double.NaN);
            return new CategoryComparison(g.Dataset, g.Category, g.Level,
                g.Base.Count == 0 ? double.NaN : g.Base.Average(), g.Cand.Count == 0 ? double.NaN : g.Cand.Average(),
                stats[i], p, p < 0.05 && stats[i]!.MeanDifference != 0);
        }).ToList();

        List<CategoryComparison> better = rows.Where(r => r.Significant && r.Stats!.MeanDifference > 0).ToList();
        List<CategoryComparison> worse = rows.Where(r => r.Significant && r.Stats!.MeanDifference < 0).ToList();
        string verdict = better.Count == 0 && worse.Count == 0
            ? "No category changed significantly."
            : $"Better: {Describe(better)}. Worse: {Describe(worse)}.";

        return new ExtractionComparisonResult(baselineRun.Name, candidateRun.Name, rows,
            candidate.SilentFailureRate - baseline.SilentFailureRate,
            candidate.FailsLoudlyRate - baseline.FailsLoudlyRate,
            verdict, unpaired);
    }

    private static string Describe(List<CategoryComparison> rows) =>
        rows.Count == 0 ? "none" : string.Join(", ", rows.Select(r => $"{r.Dataset}/{r.Category} ({r.Level})"));
}
