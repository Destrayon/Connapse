using Connapse.Eval.Checks;
using Connapse.Eval.Runs;
using Connapse.Eval.Statistics;

namespace Connapse.Eval.Reports;

/// <param name="Stats">Paired over checks both runs scored (pass = 1, fail = 0); null when fewer than two pairs.</param>
/// <param name="Unmatched">Checks scored by only one of the runs. A row with any is never marked significant,
/// because the two runs did not score the same set of checks.</param>
public sealed record CategoryComparison(
    string Dataset, string Category, string Level, double BaselineRate, double CandidateRate,
    PairedComparison? Stats, double HolmP, bool Significant, int Unmatched);

public sealed record ExtractionComparisonResult(
    string Baseline,
    string Candidate,
    IReadOnlyList<CategoryComparison> Rows,
    double SilentFailureDelta,
    double FailsLoudlyDelta,
    string Verdict,
    IReadOnlyList<string> UnpairedDatasets,
    IReadOnlyList<string> DatasetMismatches);

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
        // Same name is not enough: a dataset is compared only when both runs used the same revision and files.
        Dictionary<string, RunDatasetInfo> baseInfo = baseline.Manifest.Datasets.ToDictionary(d => d.Name, StringComparer.Ordinal);
        Dictionary<string, RunDatasetInfo> candInfo = candidate.Manifest.Datasets.ToDictionary(d => d.Name, StringComparer.Ordinal);
        List<string> mismatched = baseComplete.Intersect(candComplete)
            .Where(n => !SameRevision(baseInfo[n], candInfo[n]))
            .Order(StringComparer.Ordinal).ToList();
        List<string> shared = baseComplete.Intersect(candComplete).Except(mismatched).Order(StringComparer.Ordinal).ToList();
        List<string> unpaired = baseComplete.Union(candComplete).Except(shared).Except(mismatched).Order(StringComparer.Ordinal).ToList();

        List<(string Dataset, string Category, string Level, List<double> Base, List<double> Cand, int Unmatched)> groups = [];
        foreach (string dataset in shared)
        {
            // Pairs by check ID, level and category, so a check whose definition moved category is unmatched.
            Dictionary<(string, string, string), CheckRecord> candidateChecks = candidateRun.ReadChecks(dataset)
                .Where(c => c.Level != CheckLevel.Document && c.Outcome != CheckOutcome.Skipped)
                .ToDictionary(c => (c.CheckId, c.Level, c.Category));
            HashSet<(string, string, string)> matched = [];
            foreach (IGrouping<(string Category, string Level), CheckRecord> group in baselineRun.ReadChecks(dataset)
                .Where(c => c.Level != CheckLevel.Document && c.Outcome != CheckOutcome.Skipped)
                .GroupBy(c => (c.Category, c.Level))
                .OrderBy(g => g.Key.Category, StringComparer.Ordinal).ThenBy(g => g.Key.Level, StringComparer.Ordinal))
            {
                List<double> b = [], c = [];
                int unmatched = 0;
                foreach (CheckRecord check in group)
                {
                    if (candidateChecks.TryGetValue((check.CheckId, check.Level, check.Category), out CheckRecord? other))
                    {
                        matched.Add((check.CheckId, check.Level, check.Category));
                        b.Add(check.Outcome == CheckOutcome.Pass ? 1 : 0);
                        c.Add(other.Outcome == CheckOutcome.Pass ? 1 : 0);
                    }
                    else
                    {
                        unmatched++;
                    }
                }
                unmatched += candidateChecks.Values.Count(x => x.Category == group.Key.Category && x.Level == group.Key.Level
                    && !group.Any(g => g.CheckId == x.CheckId));
                groups.Add((dataset, group.Key.Category, group.Key.Level, b, c, unmatched));
            }
            // Categories only the candidate scored.
            foreach (IGrouping<(string Category, string Level), CheckRecord> onlyCandidate in candidateChecks.Values
                .Where(x => !groups.Any(g => g.Dataset == dataset && g.Category == x.Category && g.Level == x.Level))
                .GroupBy(x => (x.Category, x.Level)))
                groups.Add((dataset, onlyCandidate.Key.Category, onlyCandidate.Key.Level, [], [], onlyCandidate.Count()));
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
                stats[i], p, p < 0.05 && stats[i]!.MeanDifference != 0 && g.Unmatched == 0, g.Unmatched);
        }).ToList();

        List<CategoryComparison> better = rows.Where(r => r.Significant && r.Stats!.MeanDifference > 0).ToList();
        List<CategoryComparison> worse = rows.Where(r => r.Significant && r.Stats!.MeanDifference < 0).ToList();
        int unmatchedTotal = rows.Sum(r => r.Unmatched);
        string verdict = mismatched.Count > 0 || unmatchedTotal > 0
            ? $"Not comparable as a whole: {(mismatched.Count > 0 ? $"dataset revisions differ for {string.Join(", ", mismatched)}; " : "")}"
                + $"{unmatchedTotal} checks were scored by only one run. Rows with unmatched checks are never marked significant."
                + (better.Count + worse.Count > 0 ? $" Better: {Describe(better)}. Worse: {Describe(worse)}." : "")
            : better.Count == 0 && worse.Count == 0
                ? "No category changed significantly."
                : $"Better: {Describe(better)}. Worse: {Describe(worse)}.";

        return new ExtractionComparisonResult(baselineRun.Name, candidateRun.Name, rows,
            candidate.SilentFailureRate - baseline.SilentFailureRate,
            candidate.FailsLoudlyRate - baseline.FailsLoudlyRate,
            verdict, unpaired, mismatched);
    }

    private static bool SameRevision(RunDatasetInfo a, RunDatasetInfo b) =>
        a.Version == b.Version && a.FileSha256.Count == b.FileSha256.Count
        && a.FileSha256.All(p => b.FileSha256.TryGetValue(p.Key, out string? h) && h == p.Value);

    private static string Describe(List<CategoryComparison> rows) =>
        rows.Count == 0 ? "none" : string.Join(", ", rows.Select(r => $"{r.Dataset}/{r.Category} ({r.Level})"));
}
